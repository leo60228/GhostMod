﻿using Celeste.Mod;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using MonoMod.Detour;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.Ghost.Net {
    public static class GhostNetCommandsStandard {

        [GhostNetCommandField]
        public static GhostNetCommand Help = new GhostNetDCommand {
            Name = "help",
            Args = "[page] | [command]",
            Help = "Get help on how to use commands.",
            OnRun = (cmd, env, args) => {
                if (args.Length == 1) {
                    if (args[0].Type == GhostNetCommandArg.EType.Int) {
                        env.Send(Help_GetCommandPage(env, args[0].Int));
                        return;
                    }

                    env.Send(Help_GetCommandSnippet(env, args[0].String));
                    return;
                }

                env.Send(Help_GetCommandPage(env, 0));
            }
        };

        public static string Help_GetCommandPage(GhostNetCommandEnv env, int page = 0) {
            const int pageSize = 8;

            string prefix = GhostNetModule.Settings.ServerCommandPrefix;
            StringBuilder builder = new StringBuilder();

            int pages = (int) Math.Ceiling(env.Server.Commands.Count / (float) pageSize);
            if (page < 0 || pages <= page)
                throw new Exception("Page out of range!");

            for (int i = page * pageSize; i < (page + 1) * pageSize && i< env.Server.Commands.Count; i++) {
                GhostNetCommand cmd = env.Server.Commands[i];
                builder
                    .Append(prefix)
                    .Append(cmd.Name)
                    .Append(" ")
                    .Append(cmd.Args)
                    .AppendLine();
            }

            builder
                .Append("Page ")
                .Append(page + 1)
                .Append("/")
                .Append(pages);

            return builder.ToString().Trim();
        }

        public static string Help_GetCommandSnippet(GhostNetCommandEnv env, string cmdName) {
            GhostNetCommand cmd = env.Server.GetCommand(cmdName);
            if (cmd == null)
                throw new Exception($"Command {cmdName} not found!");

            return Help_GetCommandSnippet(env, cmd);
        }

        public static string Help_GetCommandSnippet(GhostNetCommandEnv env, GhostNetCommand cmd) {
            string prefix = GhostNetModule.Settings.ServerCommandPrefix;
            StringBuilder builder = new StringBuilder();

            builder
                .Append(prefix)
                .Append(cmd.Name)
                .Append(" ")
                .Append(cmd.Args)
                .AppendLine()
                .AppendLine(cmd.Help);

            return builder.ToString().Trim();
        }

        [GhostNetCommandField]
        public static GhostNetCommand Kick = new GhostNetDCommand {
            Name = "kick",
            Args = "<id>",
            Help = "OP: Kick a player from the server.",
            OnRun = (cmd, env, args) => {
                if (!env.IsOP)
                    throw new Exception("You're not OP!");
                if (args.Length != 1)
                    throw new Exception("Exactly 1 argument required!");

                int id = args[0].Int;
                if (id == 0)
                    throw new Exception("Cannot kick OP!");
                GhostNetConnection other = args[0].Connection;

                other.Dispose();
            }
        };

        [GhostNetCommandField]
        public static GhostNetCommand Broadcast = new GhostNetDCommand {
            Name = "broadcast",
            Args = "<text>",
            Help = "OP: Broadcast something as the server.",
            OnParse = GhostNetDCommand.Parsers.Everything,
            OnRun = (cmd, env, args) => {
                if (!env.IsOP)
                    throw new Exception("You're not OP!");
                if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
                    return;

                env.Server.BroadcastMChat(env.Connection, env.Frame, args[0]);
            }
        };

        [GhostNetCommandField]
        public static GhostNetCommand Teleport = new GhostNetDCommand {
            Name = "tp",
            Args = "<id>",
            Help = "Teleport to another player.",
            OnRun = (cmd, env, args) => {
                if (args.Length != 1)
                    throw new Exception("Exactly 1 argument required!");

                GhostNetFrame other = args[0].Player;
                if (string.IsNullOrEmpty(other.MPlayer.SID))
                    throw new Exception("Player in menu!");
                if (!other.UUpdate.IsValid)
                    throw new Exception("Player position not known!");

                GhostNetFrame msg = env.Send($"Teleporting to {other.MPlayer.Name}#{other.HHead.PlayerID}...");

                GhostChunkNetMSession session = new GhostChunkNetMSession();
                if (other.MPlayer.SID != env.Frame.MPlayer.SID ||
                    other.MPlayer.Mode != env.Frame.MPlayer.Mode) {
                    // Request the current session information from the other player.
                    // TODO: Make requesting info easier, add timeouts.
                    GhostNetFrame? response = null;

                    // Temporary parser to grab the response.
                    GhostNetFrameParser parse = (GhostNetConnection con, ref GhostNetFrame received) => {
                        if (received.HHead.PlayerID != other.HHead.PlayerID)
                            return;
                        if (!received.MSession.IsValid)
                            return;
                        response = received;
                    };
                    env.Server.OnParse += parse;

                    // Request an MSession.
                    args[0].Connection.SendManagement(new GhostNetFrame {
                        HHead = env.Frame.HHead, // Tell the other player who requested it.

                        MRequest = {
                            ID = GhostChunkNetMSession.Chunk
                        }
                    });

                    // Wait for the response.
                    // TODO: Timeout.
                    while (response == null)
                        Thread.Sleep(0);
                    
                    env.Server.OnParse -= parse;

                    if (response.Value.MSession.InSession)
                        session = response.Value.MSession;
                }

                env.Connection.SendManagement(new GhostNetFrame {
                    HHead = {
                        IsValid = true,
                        PlayerID = env.Frame.HHead.PlayerID
                    },

                    MPlayer = {
                        IsValid = true,
                        Name = env.Frame.MPlayer.Name,
                        SID = other.MPlayer.SID,
                        Mode = other.MPlayer.Mode,
                        Level = other.MPlayer.Level
                    },

                    // This is only sent if the two players are in incompatible sessions.
                    MSession = session,

                    // This also sends other info such as the player rotation, scale, color, ...
                    // ... but the client should know what to do.
                    UUpdate = other.UUpdate
                });

                msg.MChat.Text = $"Teleported to {other.MPlayer.Name}#{other.HHead.PlayerID}";
                env.Connection.SendManagement(msg);

            }
        };

        [GhostNetCommandField]
        public static GhostNetCommand Emote = new GhostNetDCommand {
            Name = "emote",
            Args = "<text> | i:<img> | p:<img>",
            Help =
@"Send an emote appearing over your player.
Normal text appears over your player.
i: shows images from the ""GUI"" atlas.
p: shows images from the ""Portraits"" atlas.
",
            OnParse = GhostNetDCommand.Parsers.Everything,
            OnRun = (cmd, env, args) => {
                if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
                    return;

                GhostNetFrame frame = new GhostNetFrame {
                    HHead = env.Frame.HHead,

                    MEmote = {
                        Value = args[0]
                    }
                };
                env.Server.Parse(env.Connection, ref frame);
            }
        };

    }
}
