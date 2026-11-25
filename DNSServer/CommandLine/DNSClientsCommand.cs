/*
 * Copyright (c) 2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of DNSServer <https://github.com/OpenChargingCloud/DNSServer>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP.Node.Web;

using cloud.charging.open.DNSServer.SecureShell;

using HermodDNSServer = org.GraphDefined.Vanaheimr.Hermod.DNS.DNSServer;

#endregion

namespace cloud.charging.open.DNSServer.CommandLine
{

    /// <summary>
    /// The machines that may ask the name server over SSH: listed, let in with
    /// a key, switched on or off, and taken out.
    /// </summary>
    /// <remarks>
    /// <code>
    /// dnsClients                                  every DNS client, with its keys and what it asked last
    /// dnsClients add &lt;name&gt; &lt;authorized_keys line&gt;  let a client in with a key - a new client, or one more key;
    ///                                             with cert-authority, with the user certificates of an SSH CA
    /// dnsClients remove &lt;name&gt; [&lt;fingerprint&gt;]     take a client out, or one of its keys
    /// dnsClients enable|disable &lt;name&gt;           switch a client on or off
    /// dnsClients ssh on|off                       answer DNS over SSH, or not
    /// </code>
    /// Kept in the "ssh" of the "dnsServer" section of the configuration file,
    /// and in effect at once: a client taken out has its session ended.
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class DNSClientsCommand(DNSServerCLI CLI) : ACLICommand<DNSServerCLI>(CLI),
                                                       ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "dnsClients";

        private static readonly String[] verbs = [ "add", "remove", "enable", "disable", "ssh" ];

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2)
                return verbs.Where (verb => verb.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                             Select(verb => SuggestionResponse.ParameterCompleted($"{CommandName} {verb}"));

            if (Arguments.Length == 3 && Arguments[1] is "remove" or "enable" or "disable")
                return cli.DNSServer.SSHClients.Clients.
                           Where (client => client.Name.StartsWith(Arguments[2], StringComparison.OrdinalIgnoreCase)).
                           Select(client => SuggestionResponse.ParameterCompleted($"{CommandName} {Arguments[1]} {client.Name}"));

            if (Arguments.Length == 3 && Arguments[1] == "ssh")
                return new[] { "on", "off" }.
                           Where (state => state.StartsWith(Arguments[2], StringComparison.OrdinalIgnoreCase)).
                           Select(state => SuggestionResponse.ParameterCompleted($"{CommandName} ssh {state}"));

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            var node = cli.DNSServer;

            if (Arguments.Length == 1)
            {

                if (!cli.MayDo(Permission.Read(DNSServerAccess.NameServer), CommandName, out var refusedRead))
                    return Task.FromResult<String[]>([ refusedRead ]);

                return Task.FromResult(Listed());

            }

            if (!cli.MayDo(Permission.Edit(DNSServerAccess.NameServer), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var clients = node.Settings.SSHClients.ToList();
            var verb    = Arguments[1].ToLowerInvariant();

            #region ssh on|off

            if (verb == "ssh")
            {

                if (Arguments.Length != 3 || Arguments[2].ToLowerInvariant() is not ("on" or "off"))
                    return Task.FromResult<String[]>([ Help() ]);

                var on = Arguments[2].Equals("on", StringComparison.OrdinalIgnoreCase);

                if (!node.TrySetSSHEnabled(on, out var notSaved))
                    return Task.FromResult<String[]>([ $"Not saved: {notSaved}" ]);

                return Task.FromResult<String[]>([
                           on ? node.SSHEnabled
                                    ? "DNS is answered over SSH from now on."
                                    : "DNS over SSH is switched on - but the SSH server of this node is off, so nobody can reach it yet."
                              : "DNS is no longer answered over SSH."
                       ]);

            }

            #endregion

            if (Arguments.Length < 3)
                return Task.FromResult<String[]>([ Help() ]);

            var name   = Arguments[2];
            var index  = clients.FindIndex(client => client.Name == name);

            switch (verb)
            {

                #region add <name> <authorized_keys line>

                case "add":
                {

                    if (Arguments.Length < 4)
                        return Task.FromResult<String[]>([ $"Which key? {Help()}" ]);

                    if (!DNSServerSettings.IsClientName(name))
                        return Task.FromResult<String[]>([ $"'{name}' can not name a DNS client: letters, digits and . _ @ + -, not beginning with a dot." ]);

                    if (index < 0 && clients.Any(client => String.Equals(client.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return Task.FromResult<String[]>([ $"There is a DNS client '{clients.First(client => String.Equals(client.Name, name, StringComparison.OrdinalIgnoreCase)).Name}' already: a name differs from it in more than upper and lower case." ]);

                    if (index < 0 && User_Id.TryParse(name) is User_Id userId && node.ExtAPI.TryGetUser(userId, out _))
                        return Task.FromResult<String[]>([ $"'{name}' is an account of this {node.Kind.Name} already, and a name that is both is let in as neither. Choose another name." ]);

                    var line = String.Join(" ", Arguments[3..]);

                    if (!DNSServerSettings.IsAuthorizedKeyLine(line, out var why))
                        return Task.FromResult<String[]>([ $"Not an authorized_keys line: {why}" ]);

                    if (index < 0)
                        clients.Add(new DNSClientEntry(name, [ line ]));

                    else
                    {

                        var existing = clients[index];

                        if (existing.Keys.Contains(line))
                            return Task.FromResult<String[]>([ $"The DNS client '{name}' has that key already." ]);

                        clients[index] = existing with { Keys = [ .. existing.Keys, line ] };

                    }

                    var with = AuthorizedKeysFile.TryParseLine(line, out var added) && added is not null && added.IsCertAuthority
                                   ? $"the user certificates of the CA {FingerprintOf(line)}"
                                   : FingerprintOf(line);

                    return Saved(clients, index < 0
                                              ? $"The DNS client '{name}' may ask over SSH with {with}."
                                              : $"The DNS client '{name}' may sign in with {with} as well.");

                }

                #endregion

                #region remove <name> [<fingerprint>]

                case "remove":
                case "delete":
                {

                    if (index < 0)
                        return Task.FromResult<String[]>([ $"There is no DNS client '{name}'." ]);

                    if (Arguments.Length == 3)
                    {
                        clients.RemoveAt(index);
                        return Saved(clients, $"The DNS client '{name}' is taken out.");
                    }

                    var fingerprint = Arguments[3];
                    var existing    = clients[index];
                    var matching    = existing.Keys.Where(key => FingerprintOf(key).StartsWith(fingerprint, StringComparison.Ordinal) ||
                                                                 FingerprintOf(key).StartsWith("SHA256:" + fingerprint, StringComparison.Ordinal)).
                                                    ToArray();

                    if (matching.Length != 1)
                        return Task.FromResult<String[]>([ matching.Length == 0
                                                               ? $"No key of the DNS client '{name}' has the fingerprint '{fingerprint}'."
                                                               : $"'{fingerprint}' is the beginning of {matching.Length} keys of the DNS client '{name}': say more of it." ]);

                    clients[index] = existing with { Keys = [ .. existing.Keys.Where(key => key != matching[0]) ] };

                    return Saved(clients, $"The DNS client '{name}' no longer signs in with {FingerprintOf(matching[0])}.");

                }

                #endregion

                #region enable / disable <name>

                case "enable":
                case "disable":
                {

                    if (index < 0)
                        return Task.FromResult<String[]>([ $"There is no DNS client '{name}'." ]);

                    var enabled = verb == "enable";

                    clients[index] = clients[index] with { Enabled = enabled };

                    return Saved(clients, $"The DNS client '{name}' is switched {(enabled ? "on" : "off")}.");

                }

                #endregion

                default:
                    return Task.FromResult<String[]>([ Help() ]);

            }

        }

        #endregion


        #region (private) Listed()

        private String[] Listed()
        {

            var node     = cli.DNSServer;
            var clients  = node.Settings.SSHClients;
            var lines    = new List<String> {
                               $"DNS over SSH is {(node.DNSOverSSH ? $"answered at {node.SSHURL}, subsystem {HermodDNSServer.SSHSubsystemName}" : node.Settings.SSHEnabled ? "switched on, but the SSH server is off" : "switched off")}."
                           };

            if (clients.Count == 0)
                lines.Add($"No DNS client yet: {CommandName} add <name> <authorized_keys line>. Accounts allowed to run '{DNSServerAccess.Query}' may ask as well.");

            foreach (var client in clients)
            {

                lines.Add($"{client.Name}{(client.Enabled ? "" : " (switched off)")}: {client.Keys.Count} key(s)");

                foreach (var key in client.Keys)
                    lines.Add($"    {FingerprintOf(key)}");

                lines.Add(ActivityLine(node.ActivityOf(client.Name)));

            }

            // Accounts allowed to run "dnsquery" are no DNS clients, but are answered all the same.
            foreach (var account in node.AccountsThatAsked())
            {
                lines.Add($"{account.Key} (an account of this {node.Kind.Name})");
                lines.Add(ActivityLine(account.Value));
            }

            return [ .. lines ];

        }

        #endregion

        #region (private) Saved(Clients, Said)

        private Task<String[]> Saved(IEnumerable<DNSClientEntry>  Clients,
                                     String                       Said)
        {

            if (!cli.DNSServer.TrySetSSHClients(Clients, out var notSaved))
                return Task.FromResult<String[]>([ $"Not saved: {notSaved}" ]);

            cli.DNSServer.Log.Notice($"{Said} ({cli.Caller})", "dnsserver", "ssh", "auth");

            return Task.FromResult<String[]>([ Said ]);

        }

        #endregion

        #region (private static) ActivityLine(Activity)

        /// <summary>
        /// What a login asked over SSH since this program started.
        /// </summary>
        private static String ActivityLine(DNSClientActivity Activity)

            => Activity.LastAt is DateTimeOffset at
                   ? $"    last asked    {at.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC from {Activity.LastFrom ?? "a tunnel"}: {Activity.LastQuestion ?? "no question"}; " +
                     $"{Activity.Queries} quer{(Activity.Queries == 1 ? "y" : "ies")} since the start"
                   :  "    nothing asked since the start";

        #endregion

        #region (private static) FingerprintOf(Line)

        private static String FingerprintOf(String Line)

            => AuthorizedKeysFile.TryParseLine(Line, out var key) && key is not null
                   ? $"{key.PublicKey.Sha256Fingerprint} ({key.PublicKey.Algorithm}" +
                     (key.IsCertAuthority
                          ? ", a CA" + (key.Principals.Count > 0 ? $" for {String.Join(", ", key.Principals)}" : "")
                          : "") + ")"
                   : "an unreadable key";

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [add <name> <authorized_keys line> | remove <name> [<fingerprint>] | enable|disable <name> | ssh on|off] - " +
                "the machines that may ask the name server over SSH, by their keys or by the certificates of an SSH CA (cert-authority), " +
                "and what each asked last";

        #endregion

    }

}
