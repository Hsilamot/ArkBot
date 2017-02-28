﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord.Commands;
using ArkBot.Helpers;
using ArkBot.Extensions;
using static System.FormattableString;
using System.Drawing;
using System.Text.RegularExpressions;
using QueryMaster.GameServer;
using System.Runtime.Caching;
using ArkBot.Database;

namespace ArkBot.Commands
{
    public class PlayerListCommand : ICommand
    {
        public string Name => "players";
        public string[] Aliases => new[] { "playersx", "playerlist", "playerslist" };
        public string Description => "List of players currently in-game (use **!playersx** for more details)";
        public string SyntaxHelp => null;
        public string[] UsageExamples => null;

        public bool DebugOnly => false;
        public bool HideFromCommandList => false;

        private DatabaseContextFactory<IEfDatabaseContext> _databaseContextFactory;
        private IArkContext _context;
        private IConstants _constants;

        public PlayerListCommand(DatabaseContextFactory<IEfDatabaseContext> databaseContextFactory, IArkContext context, IConstants constants)
        {
            _databaseContextFactory = databaseContextFactory;
            _context = context;
            _constants = constants;
        }

        public void Register(CommandBuilder command)
        {
            command.Parameter("optional", ParameterType.Multiple);
        }

        public async Task Run(CommandEventArgs e)
        {
            //var args = CommandHelper.ParseArgs(e, new { Extended = false }, x =>
            //    x.For(y => y.Extended, flag: true));
            var playersx = e.Message.Text.StartsWith("!playersx", StringComparison.OrdinalIgnoreCase);

            var status = await CommandHelper.GetServerStatus(_constants);

            var sb = new StringBuilder();
            if (status == null || status.Item1 == null || status.Item3 == null)
            {
                sb.AppendLine($"**Player list is currently unavailable!**");
            }
            else
            {
                var serverInfo = status.Item1;
                var playerInfo = status.Item3;
                var players = playerInfo?.Where(x => !string.IsNullOrEmpty(x.Name)).ToArray() ?? new PlayerInfo[] { };

                var m = new Regex(@"^(?<name>.+?)\s+-\s+\(v(?<version>\d+\.\d+)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline).Match(serverInfo.Name);
                var name = m.Success ? m.Groups["name"].Value : serverInfo.Name;

                sb.AppendLine($"**{name} ({serverInfo.Players - (playerInfo.Count - players.Length)}/{serverInfo.MaxPlayers})**");

                if (playersx)
                {
                    using (var db = _databaseContextFactory.Create())
                    {
                        var playerNames = players.Select(x => x.Name).ToArray();
                        var d = _context.Players.Where(x => playerNames.Contains(x.PlayerName, StringComparer.Ordinal)).Select(x =>
                        {
                            long steamId;
                            return new Tuple<Data.Player, Database.Model.User, Discord.User, long?, TimeSpan>(
                                x,
                                null,
                                null,
                                long.TryParse(x.SteamId, out steamId) ? steamId : (long?)null,
                                TimeSpan.Zero);
                        }).ToDictionary(x => x.Item1.PlayerName, StringComparer.OrdinalIgnoreCase);

                        var ids = new List<int>();
                        var steamIds = d.Values.Select(x => x.Item4).Where(x => x != null).ToArray();
                        foreach (var user in db.Users.Where(y => steamIds.Contains(y.SteamId)))
                        {
                            var item = d.Values.FirstOrDefault(x => x.Item4 == user.SteamId);
                            if (item == null) continue;

                            ids.Add(item.Item1.Id);

                            var discordUser = e.Server.GetUser((ulong)user.DiscordId);
                            var playedLastSevenDays = TimeSpan.FromSeconds(user?.Played?.OrderByDescending(x => x.Date).Take(7).Sum(x => x.TimeInSeconds) ?? 0);

                            d[item.Item1.PlayerName] = new Tuple<Data.Player, Database.Model.User, Discord.User, long?, TimeSpan>(item.Item1, user, discordUser, item.Item4, playedLastSevenDays);
                        }

                        var remaining = d.Values.Where(x => !ids.Contains(x.Item1.Id)).Where(x => x.Item4 != null).Select(x => x.Item4.Value).ToArray();
                        foreach (var user in db.Played.Where(x => x.SteamId.HasValue && remaining.Contains(x.SteamId.Value))
                            .GroupBy(x => x.SteamId)
                            .Select(x => new { key = x.Key, items = x.OrderByDescending(y => y.Date).Take(7).ToList() })
                            .ToArray())
                        {
                            var item = d.Values.FirstOrDefault(x => x.Item4 == user.key);
                            if (item == null) continue;

                            var playedLastSevenDays = TimeSpan.FromSeconds(user?.items?.Sum(x => x.TimeInSeconds) ?? 0);
                            d[item.Item1.PlayerName] = new Tuple<Data.Player, Database.Model.User, Discord.User, long?, TimeSpan>(item.Item1, item.Item2, item.Item3, item.Item4, playedLastSevenDays);
                        }

                        //var playerslist = players.Select(x => {
                        //    var extra = d.ContainsKey(x.Name) ? d[x.Name] : null;
                        //    return new
                        //    {
                        //        Steam = x.Name,
                        //        Name = extra?.Item1?.Name,
                        //        Tribe = extra?.Item1?.TribeName,
                        //        Discord = extra != null && extra.Item3 != null ? $"{extra.Item3.Name}#{extra.Item3.Discriminator}" : null,
                        //        TimeOnline = x.Time.ToStringCustom(),
                        //        PlayedLastSevenDays = extra != null && extra.Item5.TotalMinutes > 1 ? extra?.Item5.ToStringCustom() : null
                        //    };
                        //}).ToArray();

                        //sb.AppendLine("```");
                        //sb.AppendLine(FixedWidthTableHelper.ToString(playerslist, x => x
                        //    .For(y => y.TimeOnline, "Online For", alignment: 1)
                        //    .For(y => y.PlayedLastSevenDays, "Played/last 7 days", alignment: 1)));
                        //sb.AppendLine("```");

                        foreach (var player in players)
                        {
                            var extra = d.ContainsKey(player.Name) ? d[player.Name] : null;

                            sb.AppendLine($"● **{player.Name}"
                                + (extra != null && extra.Item1.Name != null ? $" ({extra.Item1.Name})" + (extra.Item1.TribeName != null ? $" [{extra.Item1.TribeName}]" : "") : "")
                                + "**"
                                + (extra != null && extra.Item3 != null ? $" - **{extra.Item3.Name}#{extra.Item3.Discriminator}**" : "")
                                + (player.Time != TimeSpan.Zero ? " (" + player.Time.ToStringCustom() + ")" : "")
                                + (extra != null && extra.Item5.TotalMinutes > 1 ? " [" + extra.Item5.ToStringCustom() + " last 7d]" : null));
                        }
                    }
                }
                else
                {
                    foreach (var player in players)
                    {
                        sb.AppendLine($"● **{player.Name}** ({player.Time.ToStringCustom()})");
                    }
                }
            }

            await CommandHelper.SendPartitioned(e.Channel, sb.ToString());
        }
    }
}