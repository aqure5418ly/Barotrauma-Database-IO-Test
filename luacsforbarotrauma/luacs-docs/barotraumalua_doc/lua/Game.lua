-- luacheck: ignore 111

--[[--
Class providing game related things, Access fields and functions like that: Game.*
]]
-- @code Game
-- @pragma nostrip

local Game = {}

--- Is the round started?
-- @realm shared
Game.RoundStarted = true

--- Is dedicated server?
-- @realm server
Game.IsDedicated = true

--- Server settings.
-- @realm server
Game.ServerSettings = true

--- Server settings.
-- @realm server
Game.Settings = true

--- ChatBox.
-- @realm server
Game.ChatBox = true

--- Send chat message to every client.
-- @realm server
function Game.SendMessage(msg, messageType, sender, character) end

--- Send traitor message.
-- @realm server
function Game.SendTraitorMessage(client, msg, missionid, type) end


--- Send direct message.
-- @realm server
function Game.SendDirectChatMessage(sendername, text, senderCharacter, chatMessageType, client, iconStyle) end

--- Send direct message.
-- @realm server
function Game.SendDirectChatMessage(chatMessage, client) end

--- True to override traitors.
-- @realm server
function Game.OverrideTraitors(override) end

--- True to override respawn sub, stops players from being respawned.
-- @realm server
function Game.OverrideRespawnSub(override) end

--- True to make wifi chat always work.
-- @realm server
function Game.AllowWifiChat(override) end

--- True to prevent headsets from transmitting wifi signals.
-- @realm server
function Game.OverrideSignalRadio(override) end

--- True to disable spam filter.
-- @realm server 
function Game.DisableSpamFilter(override) end

--- True to disable character disconnect logic, aka stop character from being automatically stunned and killed.
-- @realm server 
function Game.DisableDisconnectCharacter(override) end

--- True to allow husks to carry control to players.
-- @realm server 
function Game.EnableControlHusk(override) end

--- Log message to server logs.
-- @realm server
function Game.Log(message, ServerLogMessageType) end

--- Spawn explosion.
-- @realm shared
function Game.Explode(pos, range, force, damage, structureDamage, itemDamage, empStrength, ballastFloraStrength) end

--- Get respawn sub submarine.
--@treturn Submarine Respawn Shuttle
-- @realm shared 
function Game.GetRespawnSub() end

--- Dispatch respawn sub.
-- @realm server 
function Game.DispatchRespawnSub() end

--- Execute console command.
-- @realm shared 
function Game.ExecuteCommand(command) end

--- Starts the game.
-- @realm server 
function Game.StartGame() end

--- Ends the game.
-- @realm server
function Game.EndGame() end

--- Adds a new command, onExecute is called with a table of strings.
-- @realm shared 
function Game.AddCommand(name, help, onExecute, getValidArgs, isCheat) end

--- Assigns a command for server on execute, onExecute is called with a table of strings.
-- @realm shared 
function Game.AssignOnExecute(names, onExecute) end

--- Assigns a command for client on execute, onExecute is called with a client, mouse position and a table of strings.
-- @realm server
function Game.AssignOnClientRequestExecute(names, onExecute) end