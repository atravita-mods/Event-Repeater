using System;
using System.Collections.Generic;
using System.IO;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System.Linq;

namespace EventRepeater
{
    /// <summary>The mod entry class loaded by SMAPI.</summary>
    public class ModEntry : Mod
    {
        /*********
        ** Fields
        *********/
        /// <summary>The event IDs to forget.</summary>
        private readonly HashSet<int> EventsToForget = new();
        private readonly HashSet<string> MailToForget = new();
        private readonly HashSet<int> ResponseToForget = new();
        private Event? LastEvent;
        private readonly List<int> ManualRepeaterList = new();

        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.UpdateTicked += this.UpdateTicked;

            helper.Events.GameLoop.GameLaunched += this.OnLaunched;
            helper.ConsoleCommands.Add("eventforget", "'usage: eventforget <id>", this.ForgetManualCommand);
            helper.ConsoleCommands.Add("showevents", "'usage: Lists all completed events", this.ShowEventsCommand);
            helper.ConsoleCommands.Add("showmail", "'usage: Lists all seen mail", this.ShowMailCommand);
            helper.ConsoleCommands.Add("mailforget", "'usage: mailforget <id>", this.ForgetMailCommand);
            helper.ConsoleCommands.Add("sendme", "'usage: sendme <id>", this.SendMailCommand);
            helper.ConsoleCommands.Add("showresponse", "'usage: Lists Response IDs.  For ADVANCED USERS!!", this.ShowResponseCommand);
            helper.ConsoleCommands.Add("responseforget", "'usage: responseforget <id>'", this.ForgetResponseCommand);
            //helper.ConsoleCommands.Add("responseadd", "'usage: responseadd <id>'  Inject a question response.", ResponseAddCommand);
            helper.ConsoleCommands.Add("repeateradd", "'usage: repeateradd <id(optional)>' Create a repeatable event.  If no id is given, the last seen will be repeated.  Works on Next Day", this.ManualRepeater);
            helper.ConsoleCommands.Add("repeatersave", "'usage: repeatersave <filename>' Creates a textfile with all events you set to repeat manually.", this.SaveManualCommand);
            helper.ConsoleCommands.Add("repeaterload", "'usage: repeaterload <filename>' Loads the file you designate.", this.LoadCommand);
            helper.ConsoleCommands.Add("inject", "'usage: inject <event, mail, response> <ID>' Example: 'inject event 1324329'  Inject IDs into the game.", this.injectCommand);
        }

        private void OnLaunched(object? sender, GameLaunchedEventArgs e)
        {
            foreach (IModInfo mod in this.Helper.ModRegistry.GetAll())
            {
                // make sure it's a Content Patcher pack
                if (!mod.IsContentPack || !mod.Manifest.ContentPackFor!.UniqueID.AsSpan().Trim().Equals("Pathoschild.ContentPatcher", StringComparison.InvariantCultureIgnoreCase))
                    continue;

                // get the directory path containing the manifest.json
                // HACK: IModInfo is implemented by ModMetadata, an internal SMAPI class which
                // contains much more non-public information. Caveats:
                //   - This isn't part of the public API so it may break in future versions.
                //   - Since the class is internal, we need reflection to access the values.
                //   - SMAPI's reflection API doesn't let us reflect into SMAPI, so we need manual
                //     reflection instead.
                //   - SMAPI's data API doesn't let us access an absolute path, so we need to parse
                //     the model ourselves.

                IContentPack? modimpl = mod.GetType().GetProperty("ContentPack")!.GetValue(mod) as IContentPack;
                if (modimpl is null)
                    throw new InvalidDataException($"Couldn't grab mod from modinfo {mod.Manifest}");
                if (!modimpl.Manifest.Dependencies.Any((dep) => dep.UniqueID.AsSpan().Trim().Equals("misscoriel.eventrepeater", StringComparison.OrdinalIgnoreCase)))
                    continue;

                string? directoryPath = modimpl.DirectoryPath;
                if (directoryPath is null)
                    throw new InvalidOperationException($"Couldn't fetch the DirectoryPath property from the mod info for {mod.Manifest.Name}.");

                // May be worthwhile insisting on a dependency here?

                // read the JSON file
                IContentPack? contentPack = this.Helper.ContentPacks.CreateFake(directoryPath);
                if (contentPack.ReadJsonFile<ThingsToForget>("content.json") is not ThingsToForget model)
                    continue;
                // extract event IDs
                if (model.RepeatEvents?.Count is > 0)
                {
                    this.EventsToForget.UnionWith(model.RepeatEvents);
                    this.Monitor.Log($"Loading {model.RepeatEvents.Count} forgettable events for {mod.Manifest.UniqueID}");
                }
                if (model.RepeatMail?.Count is > 0)
                {
                    this.MailToForget.UnionWith(model.RepeatMail);
                    this.Monitor.Log($"Loading {model.RepeatMail.Count} forgettable mail for {mod.Manifest.UniqueID}");
                }
                if (model.RepeatResponse?.Count is > 0)
                {
                    this.ResponseToForget.UnionWith(model.RepeatResponse);
                    this.Monitor.Log($"Loading{model.RepeatResponse.Count} forgettable mail for {mod.Manifest.UniqueID}");
                }
            }
            this.Monitor.Log($"Loaded a grand total of\n\t{this.EventsToForget.Count} events\n\t{this.MailToForget.Count} mail\n\t{this.ResponseToForget.Count} responses");
        }

        /*********
        ** A bunch of Methods
        *********/
        /// <summary>Raised after the game begins a new day (including when the player loads a save).</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        /// 
        private void UpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (this.LastEvent is null && Game1.CurrentEvent is not null)
                this.OnEventStarted(Game1.CurrentEvent);

            this.LastEvent = Game1.CurrentEvent;
        }

        private void OnEventStarted(Event @event)
        {
            Game1.CurrentEvent.eventCommands = ExtractCommands(Game1.CurrentEvent.eventCommands, new[] { "forgetEvent", "forgetMail", "forgetResponse" }, out ISet<string> extractedCommands);

            foreach (string command in extractedCommands)
            {
                // extract command name + raw ID
                string commandName, rawId;
                {
                    string[] parts = command.Split(' ');
                    commandName = parts[0];
                    if (parts.Length != 2) // command name + ID
                    {
                        this.Monitor.Log($"The {commandName} command requires one argument (event command: {command}).", LogLevel.Warn);
                        continue;
                    }
                    rawId = parts[1];
                }

                // handle command
                switch (commandName)
                {
                    case "forgetEvent":
                        if (int.TryParse(rawId, out int eventID))
                        {
                            Game1.player.eventsSeen.Remove(eventID);
                        }
                        else
                        {
                            this.Monitor.Log($"Could not parse event ID '{rawId}' for {commandName} command.", LogLevel.Warn);
                        }

                        break;

                    case "forgetMail":
                        Game1.player.mailReceived.Remove(rawId);
                        break;

                    case "forgetResponse":
                        if (int.TryParse(rawId, out int responseID))
                        {
                            Game1.player.dialogueQuestionsAnswered.Remove(responseID);
                            //this.Monitor.Log($"Removed {responseID}", LogLevel.Debug);
                        }
                        else
                        {
                            this.Monitor.Log($"Could not parse response ID '{rawId}' for {commandName} command.", LogLevel.Warn);
                        }
                        break;

                    default:
                        this.Monitor.Log($"Unrecognized command name '{commandName}'.", LogLevel.Warn);
                        break;
                }
            }
        }
        private static string[] ExtractCommands(string[] commands, string[] commandNamesToExtract, out ISet<string> extractedCommands)
        {
            var otherCommands = new List<string>(commands.Length);
            extractedCommands = new HashSet<string>();
            foreach (string command in commands)
            {
                if (commandNamesToExtract.Any(name => command.StartsWith(name)))
                    extractedCommands.Add(command);
                else
                    otherCommands.Add(command);
            }

            return otherCommands.ToArray();
        }

        [EventPriority(EventPriority.High + 1000)]
        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            bool removed = false;
            if (this.EventsToForget.Count > 0 || this.ManualRepeaterList.Count > 0)
            {
                for (int i = Game1.player.eventsSeen.Count - 1; i >= 0; i--)
                {
                    var evt = Game1.player.eventsSeen[i];
                    if (this.EventsToForget.Contains(evt))
                    {
                        this.Monitor.Log("Repeatable Event Found! Resetting for next time! Event ID: " + evt);
                        Game1.player.eventsSeen.RemoveAt(i);
                        removed = true;
                    }
                    else if (this.ManualRepeaterList.Contains(evt))
                    {
                        this.Monitor.Log("Manual Repeater Engaged! Resetting: " + evt);
                        Game1.player.eventsSeen.RemoveAt(i);
                        removed = true;
                    }
                }
            }
            if (!removed)
            {
                this.Monitor.Log("No repeatable events were removed");
            }

            removed = false;
            if (this.MailToForget.Count > 0)
            {
                for (int i = Game1.player.mailReceived.Count - 1; i >= 0; i--)
                {
                    var msg = Game1.player.mailReceived[i];
                    if (this.MailToForget.Contains(msg))
                    {
                        this.Monitor.Log("Repeatable Mail found!  Resetting: " + msg);
                        Game1.player.mailReceived.RemoveAt(i);
                        removed = true;
                    }
                }
            }
            if (!removed)
            {
                this.Monitor.Log("No repeatable mail found for removal.");
            }

            removed = false;
            if (this.ResponseToForget.Count > 0)
            {
                for (int i = Game1.player.dialogueQuestionsAnswered.Count - 1; i >= 0; i--)
                {
                    var response = Game1.player.dialogueQuestionsAnswered[i];
                    if (this.ResponseToForget.Contains(response))
                    {
                        Game1.player.dialogueQuestionsAnswered.RemoveAt(i);
                        this.Monitor.Log("Repeatable Response Found! Resetting: " + response);
                        removed = true;
                    }
                }
            }
            if (!removed)
            {
                this.Monitor.Log("No repeatable responses found.");
            }
        }

        private void ManualRepeater(string command, string[] parameters)
        {
            //This command will set a manual repeat to a list and save the event IDs to a file in the SDV folder.  
            //The first thing to do is create a list
            List<int> eventsSeenList = new();
            //Populate that list with Game1.eventseen
            foreach(int eventID in Game1.player.eventsSeen)
            {
                eventsSeenList.Add(eventID);
            }
            //Check to see if an EventID was added in the command.. If not, then add the last ID on the list
            if (parameters.Length == 0)
            {
                try
                {
                    int lastEvent = eventsSeenList[^1];
                    Game1.player.eventsSeen.RemoveAt(Game1.player.eventsSeen.Count - 1); // Remove the last ID.
                    this.ManualRepeaterList.Add(lastEvent);//Adds to the Manual List
                    this.Monitor.Log($"{lastEvent} has been added to Manual Repeater", LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log(ex.Message, LogLevel.Warn);
                }
            }
            else
            {
                try
                {
                    foreach (string? param in parameters)
                    {
                        if (int.TryParse(param, out int evt))
                        {
                            this.ManualRepeaterList.Add(evt);
                            Game1.player.eventsSeen.Remove(evt);
                            this.Monitor.Log($"{evt} has been added to Manual Repeater", LogLevel.Debug);
                        }
                        else
                        {
                            this.Monitor.Log($"{param} was not a valid event!", LogLevel.Warn);
                        }
                    }
                    
                }
                catch(Exception ex)
                {
                    this.Monitor.Log(ex.Message, LogLevel.Warn);
                }
            }

        }
        private void SaveManualCommand(string command, string[] parameters)
        {
            //This will allow you to save your repeatable events from the manual repeater
            //Create Directory
            Directory.CreateDirectory(Environment.CurrentDirectory + "\\ManualRepeaterFiles");
            string savePath = Environment.CurrentDirectory + "\\ManualRepeaterFiles\\" + parameters[0] + ".txt"; //Saves file in the name you designate
            string[] parse = new string[this.ManualRepeaterList.Count];
            int i = 0; //start at beginning of manual list
            foreach(var item in this.ManualRepeaterList)//Converts the Manual list to a string array
            {
                parse[i] = item.ToString();
                i++;
            }
            File.WriteAllLines(savePath, parse);
            this.Monitor.Log($"Saved file to {savePath}", LogLevel.Debug);
        }
        private void LoadCommand(string command, string[] parameters)
        {
            //This will allow you to load a saved manual repeater file
            //First Check to see if you have the Directory
            if(Directory.Exists(Environment.CurrentDirectory + "\\ManualRepeaterFiles"))
            {
                string loadPath = Environment.CurrentDirectory + "\\ManualRepeaterFiles\\" + parameters[0] + ".txt"; //loads the filename you choose
                //Save all strings to a List
                List<string> FileIDs = new List<string>();
                FileIDs.AddRange(File.ReadAllLines(loadPath));
                //Transfer all items to ManualList and convert to int
                foreach(string eventID in FileIDs)
                {
                    int parse = Int32.Parse(eventID);
                    this.ManualRepeaterList.Add(parse);
                    Game1.player.eventsSeen.Remove(parse);
                }
                this.Monitor.Log($"{parameters[0]} loaded!", LogLevel.Debug);
            }
        }

        private void ForgetManualCommand(string command, string[] parameters)
        {
            if (parameters.Length == 0) return;
            try
            {
                int eventToForget = int.Parse(parameters[0]);
                Game1.player.eventsSeen.Remove(eventToForget);
                this.Monitor.Log("Forgetting event id: " + eventToForget, LogLevel.Debug);

            }
            catch (Exception) { }
        }
        private void ShowEventsCommand(string command, string[] parameters)
        {
            string eventsSeen = "Events seen: ";
            foreach (var e in Game1.player.eventsSeen)
            {
                eventsSeen += e + ", ";
            }
            this.Monitor.Log(eventsSeen, LogLevel.Debug);
        }
        private void ShowMailCommand(string command, string[] parameters)
        {
            string mailSeen = "Mail Seen: ";
            foreach (var e in Game1.player.mailReceived)
            {
                mailSeen += e + ", ";
            }
            this.Monitor.Log(mailSeen, LogLevel.Debug);
        }
        private void ForgetMailCommand(string command, string[] parameters)
        {
            if (parameters.Length == 0) return;
            try
            {
                string MailToForget = parameters[0];
                Game1.player.mailReceived.Remove(MailToForget);
                this.Monitor.Log("Forgetting event id: " + MailToForget, LogLevel.Debug);

            }
            catch (Exception) { }
        }
        private void SendMailCommand(string command, string[] parameters)
        {
            if (parameters.Length == 0) return;
            try
            {
                string MailtoSend = parameters[0];
                Game1.addMailForTomorrow(MailtoSend);
                this.Monitor.Log("Check Mail Tomorrow!! Sending: " + MailtoSend, LogLevel.Debug);

            }
            catch (Exception) { }
        }
        private void ShowResponseCommand(string command, string[] parameters)
        {
            string dialogueQuestionsAnswered = "Response IDs: ";
            foreach (var e in Game1.player.dialogueQuestionsAnswered)
            {
                dialogueQuestionsAnswered += e + ", ";
            }
            this.Monitor.Log(dialogueQuestionsAnswered, LogLevel.Debug);
        }
        private void ForgetResponseCommand(string command, string[] parameters)
        {
            if (parameters.Length == 0) return;
            try
            {
                int responseToForget = int.Parse(parameters[0]);
                Game1.player.dialogueQuestionsAnswered.Remove(responseToForget);
                this.Monitor.Log("Forgetting Response ID: " + responseToForget, LogLevel.Debug);

            }
            catch (Exception) { }
        }
        private void ResponseAddCommand(string command, string[] parameters)
        {
            if (parameters.Length == 0) return;
            try
            {
                int responseAdd = int.Parse(parameters[0]);
                Game1.player.dialogueQuestionsAnswered.Add(responseAdd);
                this.Monitor.Log("Injecting Response ID: " + responseAdd, LogLevel.Debug);
            }
            catch (Exception) { }
                        
        }
        private void injectCommand(string command, string[] parameters)
        {
            ///This will replace ResponseAdd in order to inject a more versitile code
            ///function: inject <type> <ID> whereas type is event, response, mail
            ///this will not have an indicator of existing events, however will look for the ID in the appropriate list.
            ///
            if (parameters.Length == 0) return;
            if (parameters.Length == 1)
            {
                if (parameters[0] == "response")
                {
                    this.Monitor.Log("No response ID entered.  Please input a response ID", LogLevel.Error);
                }
                if (parameters[0] == "mail")
                {
                    this.Monitor.Log("No mail ID entered.  Please input a mail ID", LogLevel.Error);
                }
                if (parameters[0] == "event")
                {
                    this.Monitor.Log("No event ID entered.  Please input a event ID", LogLevel.Error);
                }
            }
            if (parameters.Length == 2)
            {
                //check for existing IDs
                if (parameters[0] == "event")
                {
                    int parameterParse = int.Parse(parameters[1]);
                    if(Game1.player.eventsSeen.Contains(parameterParse))
                    {
                        this.Monitor.Log($"{parameters[1]} Already exists within seen events.", LogLevel.Warn);
                        return;
                    }
                    else
                    {
                        Game1.player.eventsSeen.Add(parameterParse);
                        this.Monitor.Log($"{parameters[1]} has been added to the seen events list.", LogLevel.Debug);
                        return;
                    }
                }
                if (parameters[0] == "response")
                {
                    int parameterParse = int.Parse(parameters[1]);
                    if(Game1.player.dialogueQuestionsAnswered.Contains(parameterParse))
                    {
                        this.Monitor.Log($"{parameters[1]} Already exists within the response list.", LogLevel.Warn);
                        return;

                    }
                    else
                    {
                        Game1.player.dialogueQuestionsAnswered.Add(parameterParse);
                        this.Monitor.Log($"{parameters[1]} has been added to the response list.", LogLevel.Debug);
                        return;

                    }
                }
                if (parameters[0] == "mail")
                {
                    if(Game1.player.mailReceived.Contains(parameters[1]))
                    {
                        this.Monitor.Log($"{parameters[1]} Already exists within seen events.", LogLevel.Warn);
                        return;
                    }
                    else
                    {
                        Game1.player.mailReceived.Add(parameters[1]);
                        this.Monitor.Log($"{parameters[1]} has been added to the seen events list.", LogLevel.Debug);
                        return;

                    }
                }



            }
        }


    }
}