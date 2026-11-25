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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.DNSServer.CommandLine;

using HermodDNSServer = org.GraphDefined.Vanaheimr.Hermod.DNS.DNSServer;

#endregion

namespace cloud.charging.open.DNSServer.Tests
{

    /// <summary>
    /// DNS over SSH: a DNS server whose SSH server is on, asked as a charging
    /// station asks it - signed in under its name with its own key, over the
    /// subsystem "dns@open.charging.cloud" - and by accounts of the node.
    /// </summary>
    /// <remarks>
    /// Nothing here leaves this machine: every server asked is one started here.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public class DNSOverSSHTests
    {

        #region Data

        private readonly List<DNSServerNode>  nodes        = [];
        private readonly List<String>         directories  = [];

        #endregion

        #region TearDown

        [TearDown]
        public async Task TearDown()
        {

            foreach (var node in nodes)
            {
                try { await node.DisposeAsync(); } catch { }
            }

            nodes.Clear();

            foreach (var directory in directories)
            {
                try { Directory.Delete(directory, true); } catch { }
            }

            directories.Clear();

        }

        #endregion


        #region (private) Started(Section = null)

        /// <summary>
        /// A DNS server on the loopback address, DNS on ports the system chose,
        /// its SSH server on - started, with one A record to ask for.
        /// </summary>
        private async Task<DNSServerNode> Started(JObject? Section = null)
        {

            var directory = Path.Combine(Path.GetTempPath(), "DNSServerTests-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);
            directories.Add(directory);

            var section = new JObject(
                              new JProperty("listen",   "127.0.0.1"),
                              new JProperty("records",  new JArray("www.example.org. 600 IN A 192.0.2.10"))
                          );

            if (Section is not null)
                section.Merge(Section);

            var file = Path.Combine(directory, WWCPConfigFile.DefaultFileName);

            File.WriteAllText(file, new JObject(new JProperty(DNSServerSettings.SectionName, section)).ToString());

            var node = new DNSServerNode(
                           HTTPPort:          TestPorts.Free(),
                           AccountsPath:      Path.Combine(directory, "accounts"),
                           ConfigFile:        new WWCPConfigFile(file),
                           CertificatesPath:  Path.Combine(directory, "certificates"),
                           LogToConsole:      false,
                           BridgeDebugLog:    false,
                           SSH:               new SSHSettings(Enabled: true, Port: TestPorts.Free()),
                           CommandLine:       settings => settings with { Port = IPPort.Zero }
                       );

            nodes.Add(node);

            await node.Start();

            return node;

        }

        #endregion

        #region (private static) SignIn(Node, Username, Key)

        /// <summary>
        /// Signed in as the given login with the given key, against the host key
        /// the node shows - over SSH that key is the whole of the server's
        /// authentication.
        /// </summary>
        private static Task<SSHClient> SignIn(DNSServerNode  Node,
                                              String         Username,
                                              ISshHostKey    Key)

            => SSHClient.Connect(
                   "127.0.0.1",
                   Node.SSHPort!.Value,
                   new SSHClientOptions {
                       Username       = User_Id.Parse(Username),
                       Credentials    = [ Key ],
                       VerifyHostKey  = blob => Node.SSHHostKey is String hostKey &&
                                                hostKey.EndsWith(SshFingerprint.Sha256(blob), StringComparison.Ordinal)
                   }
               ).AsTask();

        #endregion

        #region (private static) Ask(SSH, Name)

        /// <summary>
        /// An A query for the given name over the DNS subsystem of the given
        /// connection, and the addresses of its answer.
        /// </summary>
        private static async Task<(DNSResponseCodes ResponseCode, String[] Addresses)> Ask(SSHClient  SSH,
                                                                                            String     Name)
        {

            await using var dns = new DNSSSHClient(_ => ValueTask.FromResult(SSH),
                                                   QueryTimeout: TimeSpan.FromSeconds(10));

            var answer = await dns.Query(DomainName.Parse(Name), [ DNSResourceRecordTypes.A ]);

            return (answer.ResponseCode,
                    [ .. answer.Answers.OfType<A>().Select(a => a.IPv4Address.ToString()) ]);

        }

        #endregion

        #region (private static) KeyLine(Key, Comment = null)

        private static String KeyLine(ISshHostKey Key, String? Comment = null)

            => Comment is null
                   ? SshPublicKey.FromHostKey(Key).ToAuthorizedKeyLine()
                   : SshPublicKey.FromHostKey(Key, Comment).ToAuthorizedKeyLine();

        #endregion

        #region (private static) AtTheConsole(Node, Line)

        private static async Task<String[]> AtTheConsole(DNSServerNode  Node,
                                                         String         Line)
        {

            using var cli    = new DNSServerCLI(Node);
            var       lines  = await cli.Execute(Line);

            TestContext.Out.WriteLine($"> {Line}");
            TestContext.Out.WriteLine(String.Join(Environment.NewLine, lines));

            return lines;

        }

        #endregion

        #region (private static) AccountIn(Node, Name, Role, Key)

        /// <summary>
        /// An account of the given name in the group of the given role, let in
        /// over SSH with the given key.
        /// </summary>
        private static async Task AccountIn(DNSServerNode  Node,
                                            User_Id        Name,
                                            String         Role,
                                            ISshHostKey    Key)
        {

            Assert.That(Node.ExtAPI.TryGetOrganization(Organization_Id.Parse(Node.Kind.Organization), out var organization) &&
                        organization is Organization, Is.True);

            await Node.ExtAPI.CreateUser(Name,
                                         I18NString.Create(Languages.en, Name.ToString()),
                                         SimpleEMailAddress.Parse($"{Name}@localhost"),
                                         User2OrganizationEdgeLabel.IsMember,
                                         (Organization) organization!,
                                         Password:                  "Not-Used-Here-1",
                                         SkipDefaultNotifications:  true,
                                         SkipNewUserEMail:          true,
                                         SkipNewUserNotifications:  true,
                                         AcceptedEULA:              DateTimeOffset.UtcNow.AddSeconds(-1),
                                         IsAuthenticated:           true);

            Assert.That(Node.ExtAPI.TryGetUser(Name, out var account) && account is User, Is.True);
            Assert.That(Node.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group) && group is UserGroup, Is.True, $"no group '{Role}'");

            await Node.ExtAPI.AddUserToUserGroup((User) account!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!);

            var added = await Node.AuthorizeSSHKey(Name, KeyLine(Key));
            Assert.That(added.IsAdded, Is.True, added.Reason);

        }

        #endregion

        #region (private static) WithinASecond(Condition)

        /// <summary>
        /// The server counts a query as it arrives, which may be a moment after
        /// the answer was read: give it that moment.
        /// </summary>
        private static async Task<Boolean> WithinASecond(Func<Boolean> Condition)
        {

            for (var i = 0; i < 50; i++)
            {
                if (Condition())
                    return true;
                await Task.Delay(20);
            }

            return Condition();

        }

        #endregion


        #region ADNSClient_IsAnsweredOverSSH_AsOverUDP()

        /// <summary>
        /// A charging station signed in under its own name with its own key asks
        /// over the subsystem, and is answered with the records the server
        /// answers over UDP and TCP with - and what it asked is counted beside
        /// it, in the status and on the DNS clients page alike.
        /// </summary>
        [Test]
        public async Task ADNSClient_IsAnsweredOverSSH_AsOverUDP()
        {

            var node  = await Started();
            var key   = SshHostKey.GenerateEd25519();

            Assert.That(node.DNSOverSSH, Is.True);
            Assert.That(node.SSHSubsystems.Select(subsystem => subsystem.Name), Does.Contain(HermodDNSServer.SSHSubsystemName));

            Assert.That(node.TrySetSSHClients([ new DNSClientEntry("cs001", [ KeyLine(key, "cs001") ]) ], out var error),
                        Is.True, error);

            await using var ssh   = await SignIn(node, "cs001", key);

            var (found, addresses)  = await Ask(ssh, "www.example.org");
            var (missing, _)        = await Ask(ssh, "nothere.example.org");

            Assert.Multiple(() => {
                Assert.That(found,      Is.EqualTo(DNSResponseCodes.NoError));
                Assert.That(addresses,  Is.EqualTo(new[] { "192.0.2.10" }));
                Assert.That(missing,    Is.EqualTo(DNSResponseCodes.NameError));
            });

            Assert.That(await WithinASecond(() => node.ActivityOf("cs001").Queries == 2), Is.True,
                        "the queries of cs001 were not counted");

            var shown   = node.SSHJSON();
            var cs001   = (JObject) shown["clients"]![0]!["activity"]!;

            Assert.Multiple(() => {
                Assert.That(shown["served"]!.Value<Boolean>(),           Is.True);
                Assert.That(shown["subsystem"]!.Value<String>(),         Is.EqualTo("dns@open.charging.cloud"));
                Assert.That(cs001["queries"]!.Value<Int64>(),            Is.EqualTo(2));
                Assert.That(cs001["lastQuestion"]!.Value<String>(),      Is.EqualTo("nothere.example.org. A"));
                Assert.That(cs001["lastFrom"]!.Value<String>(),          Does.StartWith("127.0.0.1"));
                Assert.That((JArray) shown["accounts"]!,                 Is.Empty);
                Assert.That(node.StatusJSON()["ssh"]!["clients"]!.Value<Int32>(), Is.EqualTo(1));
            });

            // And it was written down where the client list lives.
            Assert.That(File.ReadAllText(node.ConfigFile.Path), Does.Contain("cs001"));

        }

        #endregion

        #region TheDNSClientsAreShownWithTheFingerprintsOfTheirKeys()

        /// <summary>
        /// What the DNS clients page reads: each key with what it is - its
        /// algorithm, its fingerprint as ssh-keygen -l says it, its comment and
        /// its options - beside the line it was given as.
        /// </summary>
        [Test]
        public async Task TheDNSClientsAreShownWithTheFingerprintsOfTheirKeys()
        {

            var node  = await Started();
            var key   = SshHostKey.GenerateEd25519();
            var line  = "from=\"192.168.1.0/24\" " + KeyLine(key, "cs001");

            Assert.That(node.TrySetSSHClients([ new DNSClientEntry("cs001", [ line ], Enabled: false) ], out var error),
                        Is.True, error);

            var client  = (JObject) node.SSHJSON()["clients"]![0]!;
            var shown   = (JObject) client["keys"]![0]!;

            Assert.Multiple(() => {
                Assert.That(client["name"]!.Value<String>(),       Is.EqualTo("cs001"));
                Assert.That(client["enabled"]!.Value<Boolean>(),   Is.False);
                Assert.That(shown["line"]!.Value<String>(),        Is.EqualTo(line));
                Assert.That(shown["algorithm"]!.Value<String>(),   Is.EqualTo("ssh-ed25519"));
                Assert.That(shown["fingerprint"]!.Value<String>(), Is.EqualTo(SshPublicKey.FromHostKey(key).Sha256Fingerprint));
                Assert.That(shown["comment"]!.Value<String>(),     Is.EqualTo("cs001"));
                Assert.That(shown["options"]!.Values<String>(),    Does.Contain("from=\"192.168.1.0/24\""));
            });

        }

        #endregion

        #region AStranger_AndASwitchedOffClient_AreNotLetIn()

        [Test]
        public async Task AStranger_AndASwitchedOffClient_AreNotLetIn()
        {

            var node      = await Started();
            var key       = SshHostKey.GenerateEd25519();
            var off       = SshHostKey.GenerateEd25519();
            var stranger  = SshHostKey.GenerateEd25519();

            Assert.That(node.TrySetSSHClients([ new DNSClientEntry("cs001", [ KeyLine(key) ]),
                                                new DNSClientEntry("cs002", [ KeyLine(off) ], Enabled: false) ], out var error),
                        Is.True, error);

            Assert.That(async () => await SignIn(node, "cs001", stranger), Throws.Exception, "a key cs001 does not have");
            Assert.That(async () => await SignIn(node, "cs003", key),      Throws.Exception, "a name that is no DNS client");
            Assert.That(async () => await SignIn(node, "cs002", off),      Throws.Exception, "a DNS client switched off");

        }

        #endregion

        #region ATakenOutClient_HasItsSessionEnded()

        /// <summary>
        /// A client taken out while it is connected is ended at once, not at the
        /// next check: its next query is not answered.
        /// </summary>
        [Test]
        public async Task ATakenOutClient_HasItsSessionEnded()
        {

            var node  = await Started();
            var key   = SshHostKey.GenerateEd25519();

            Assert.That(node.TrySetSSHClients([ new DNSClientEntry("cs001", [ KeyLine(key) ]) ], out var error), Is.True, error);

            await using var ssh = await SignIn(node, "cs001", key);

            Assert.That((await Ask(ssh, "www.example.org")).Addresses, Is.EqualTo(new[] { "192.0.2.10" }));

            Assert.That(node.TrySetSSHClients([], out error), Is.True, error);

            DNSResponseCodes? after = null;

            try
            {
                after = (await Ask(ssh, "www.example.org")).ResponseCode;
            }
            catch (Exception)
            { }

            Assert.That(after, Is.Not.EqualTo(DNSResponseCodes.NoError), "a client taken out was still answered");

        }

        #endregion

        #region SwitchedOff_IsNotServed_AndSwitchedOnAgain()

        [Test]
        public async Task SwitchedOff_IsNotServed_AndSwitchedOnAgain()
        {

            var node  = await Started(new JObject(new JProperty("ssh", new JObject(new JProperty("enabled", false)))));
            var key   = SshHostKey.GenerateEd25519();

            Assert.That(node.DNSOverSSH, Is.False);
            Assert.That(node.SSHSubsystems.Select(subsystem => subsystem.Name), Does.Not.Contain(HermodDNSServer.SSHSubsystemName));

            Assert.That(node.TrySetSSHClients([ new DNSClientEntry("cs001", [ KeyLine(key) ]) ], out var error), Is.True, error);
            Assert.That(async () => await SignIn(node, "cs001", key), Throws.Exception, "let in while DNS over SSH is off");

            Assert.That(node.TrySetSSHEnabled(true, out error), Is.True, error);

            Assert.That(node.DNSOverSSH, Is.True);
            Assert.That(File.ReadAllText(node.ConfigFile.Path), Does.Contain("\"enabled\": true"));

            await using var ssh = await SignIn(node, "cs001", key);

            Assert.That((await Ask(ssh, "www.example.org")).Addresses, Is.EqualTo(new[] { "192.0.2.10" }));

        }

        #endregion

        #region AnAccount_IsAnswered_OnlyWithThePermission()

        /// <summary>
        /// An account asks over SSH with the permission "dnsquery:run", which
        /// the role "dnsclient" gives - and an operator, who may look at
        /// everything, is refused the subsystem.
        /// </summary>
        [Test]
        public async Task AnAccount_IsAnswered_OnlyWithThePermission()
        {

            var node      = await Started();
            var resolver  = SshHostKey.GenerateEd25519();
            var operator_ = SshHostKey.GenerateEd25519();

            await AccountIn(node, User_Id.Parse("resolver"), DNSServerAccess.DNSClient.Name, resolver);
            await AccountIn(node, User_Id.Parse("watcher"),  DNSServerAccess.Operator. Name, operator_);

            await using var asking   = await SignIn(node, "resolver", resolver);
            await using var watching = await SignIn(node, "watcher",  operator_);

            Assert.That((await Ask(asking, "www.example.org")).Addresses, Is.EqualTo(new[] { "192.0.2.10" }));

            await using var refused = new DNSSSHClient(_ => ValueTask.FromResult(watching));

            Assert.That(async () => await refused.ConnectAsync(), Throws.InstanceOf<SshSubsystemRefusedException>());

            Assert.That(await WithinASecond(() => node.ActivityOf("resolver").Queries == 1), Is.True);

            var accounts = (JArray) node.SSHJSON()["accounts"]!;

            Assert.Multiple(() => {
                Assert.That(accounts.Select(account => account["name"]!.Value<String>()), Is.EqualTo(new[] { "resolver" }));
                Assert.That(accounts[0]!["activity"]!["queries"]!.Value<Int64>(),         Is.EqualTo(1));
            });

        }

        #endregion

        #region AtTheConsole_ClientsAreAdded_Listed_AndTakenOut()

        [Test]
        public async Task AtTheConsole_ClientsAreAdded_Listed_AndTakenOut()
        {

            var node   = await Started();
            var key    = SshHostKey.GenerateEd25519();
            var other  = SshHostKey.GenerateEd25519();

            var none    = await AtTheConsole(node, "dnsClients");
            var added   = await AtTheConsole(node, $"dnsClients add cs001 {KeyLine(key, "cs001")}");
            var more    = await AtTheConsole(node, $"dnsClients add cs001 {KeyLine(other)}");
            var twice   = await AtTheConsole(node, $"dnsClients add cs001 {KeyLine(other)}");
            var bad     = await AtTheConsole(node, "dnsClients add cs002 no key at all");

            await using (var ssh = await SignIn(node, "cs001", key))
                Assert.That((await Ask(ssh, "www.example.org")).Addresses, Is.EqualTo(new[] { "192.0.2.10" }));

            Assert.That(await WithinASecond(() => node.ActivityOf("cs001").Queries == 1), Is.True);

            var listed  = await AtTheConsole(node, "dnsClients");
            var status  = await AtTheConsole(node, "dnsServer");
            var keyOut  = await AtTheConsole(node, $"dnsClients remove cs001 {SshPublicKey.FromHostKey(other).Sha256Fingerprint}");
            var off     = await AtTheConsole(node, "dnsClients disable cs001");
            var sshOff  = await AtTheConsole(node, "dnsClients ssh off");
            var gone    = await AtTheConsole(node, "dnsClients remove cs001");

            Assert.Multiple(() => {

                Assert.That(none,    Has.Some.StartsWith("No DNS client yet: dnsClients add <name> <authorized_keys line>."));
                Assert.That(added,   Has.Some.StartsWith("The DNS client 'cs001' may ask over SSH with SHA256:"));
                Assert.That(more,    Has.Some.StartsWith("The DNS client 'cs001' may sign in with SHA256:"));
                Assert.That(twice,   Has.Some.EqualTo("The DNS client 'cs001' has that key already."));
                Assert.That(bad,     Has.Some.StartsWith("Not an authorized_keys line:"));

                Assert.That(listed,  Has.Some.StartsWith("DNS over SSH is answered at ssh://"));
                Assert.That(listed,  Has.Some.EqualTo("cs001: 2 key(s)"));
                Assert.That(listed,  Has.Some.Contains("www.example.org. A; 1 query since the start"));
                Assert.That(status,  Has.Some.StartsWith("over SSH        ssh://").And.Some.Contains("1 DNS client(s)"));

                Assert.That(keyOut,  Has.Some.StartsWith("The DNS client 'cs001' no longer signs in with SHA256:"));
                Assert.That(off,     Has.Some.EqualTo("The DNS client 'cs001' is switched off."));
                Assert.That(sshOff,  Has.Some.EqualTo("DNS is no longer answered over SSH."));
                Assert.That(gone,    Has.Some.EqualTo("The DNS client 'cs001' is taken out."));

            });

            Assert.That(node.DNSOverSSH,          Is.False);
            Assert.That(node.Settings.SSHClients, Is.Empty);

        }

        #endregion

        #region TheSectionRefusesWhatCannotBe(Section, Says)

        [TestCase("""{ "ssh": [] }""",                                                                      "is no JSON object")]
        [TestCase("""{ "ssh": { "enabled": "yes" } }""",                                                    "neither true nor false")]
        [TestCase("""{ "ssh": { "clients": {} } }""",                                                      "is a list of")]
        [TestCase("""{ "ssh": { "clients": [ { "name": ".cs001" } ] } }""",                                "can not name a DNS client")]
        [TestCase("""{ "ssh": { "clients": [ { "name": "cs001", "keys": [ "no key at all" ] } ] } }""",   "authorized_keys")]
        [TestCase("""{ "ssh": { "clients": [ { "name": "cs001" }, { "name": "CS001" } ] } }""",            "listed twice")]
        public void TheSectionRefusesWhatCannotBe(String Section, String Says)
        {

            Assert.That(DNSServerSettings.TryParse(JObject.Parse(Section), out _, out var error), Is.False);
            Assert.That(error, Does.Contain(Says));

        }

        #endregion

        #region TheSectionIsReadAndWrittenAlike()

        [Test]
        public void TheSectionIsReadAndWrittenAlike()
        {

            var key   = SshPublicKey.FromHostKey(SshHostKey.GenerateEd25519(), "cs001").ToAuthorizedKeyLine();
            var json  = JObject.Parse($$"""{ "ssh": { "enabled": false, "clients": [ { "name": "cs001", "keys": [ "{{key}}" ], "enabled": false } ] } }""");

            Assert.That(DNSServerSettings.TryParse(json, out var settings, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(settings!.SSHEnabled,                Is.False);
                Assert.That(settings.SSHClients.Single().Name,   Is.EqualTo("cs001"));
                Assert.That(settings.SSHClients.Single().Keys,   Is.EqualTo(new[] { key }));
                Assert.That(settings.SSHClients.Single().Enabled, Is.False);
                Assert.That(JToken.DeepEquals(settings.ToJSON()["ssh"], json["ssh"]), Is.True, settings.ToJSON()["ssh"]!.ToString());
            });

            Assert.That(DNSServerSettings.TryParse(new JObject(), out var defaults, out _), Is.True);
            Assert.That(defaults!.SSHEnabled, Is.True, "DNS over SSH is on unless the section says otherwise");

        }

        #endregion

    }

}
