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

using System.Net.Sockets;
using System.Buffers.Binary;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.DNSServer.CommandLine;

#endregion

namespace cloud.charging.open.DNSServer.Tests
{

    /// <summary>
    /// A DNS server, started on the loopback address and on ports the system
    /// chose, told at its console what to answer, and asked over UDP and TCP.
    /// </summary>
    /// <remarks>
    /// Nothing here leaves this machine: every server asked is one started here.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public class DNSServerNodeTests
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


        #region (private) Directory() / Started(Directory, Section = null)

        private String NewDirectory()
        {

            var directory = Path.Combine(Path.GetTempPath(), "DNSServerTests-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);
            directories.Add(directory);

            return directory;

        }

        private static String ConfigFileIn(String Directory)

            => Path.Combine(Directory, WWCPConfigFile.DefaultFileName);

        /// <summary>
        /// A DNS server on the loopback address, DNS on ports the system chose,
        /// in the given directory - started.
        /// </summary>
        private async Task<DNSServerNode> Started(String    Directory,
                                                  JObject?  Section = null)
        {

            var file = ConfigFileIn(Directory);

            if (!File.Exists(file) || Section is not null)
            {

                var section = new JObject(
                                  new JProperty("listen",  "127.0.0.1")
                              );

                if (Section is not null)
                    section.Merge(Section);

                File.WriteAllText(file, new JObject(new JProperty(DNSServerSettings.SectionName, section)).ToString());

            }

            var node = new DNSServerNode(
                           HTTPPort:          TestPorts.Free(),
                           AccountsPath:      Path.Combine(Directory, "accounts"),
                           ConfigFile:        new WWCPConfigFile(file),
                           CertificatesPath:  Path.Combine(Directory, "certificates"),
                           LogToConsole:      false,
                           BridgeDebugLog:    false,
                           SSH:               new SSHSettings(Enabled: false),

                           // Port 0: the system chooses, for UDP and TCP each.
                           CommandLine:       settings => settings with { Port = IPPort.Zero }
                       );

            nodes.Add(node);

            await node.Start();

            return node;

        }

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

        #region (private static) Ask(Node, Name, OverTCP = false)

        /// <summary>
        /// An A query for the given name, sent as any resolver would, and the
        /// response code and IPv4 addresses of the answer.
        /// </summary>
        private static async Task<(Int32 ResponseCode, Boolean Authoritative, String[] Addresses)> Ask(DNSServerNode  Node,
                                                                                                         String         Name,
                                                                                                         Boolean        OverTCP = false)
        {

            var query = new List<Byte> { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

            foreach (var label in Name.TrimEnd('.').Split('.'))
            {
                query.Add((Byte) label.Length);
                query.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
            }

            query.AddRange([ 0x00, 0x00, 0x01, 0x00, 0x01 ]);

            Byte[] response;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            if (OverTCP)
            {

                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", Node.TCPSocket!.Value.Port.ToUInt16(), timeout.Token);

                var stream = tcp.GetStream();
                var length = new Byte[2];

                BinaryPrimitives.WriteUInt16BigEndian(length, (UInt16) query.Count);
                await stream.WriteAsync(length,          timeout.Token);
                await stream.WriteAsync(query.ToArray(), timeout.Token);

                await stream.ReadExactlyAsync(length,    timeout.Token);
                response = new Byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
                await stream.ReadExactlyAsync(response,  timeout.Token);

            }
            else
            {

                using var udp = new UdpClient(AddressFamily.InterNetwork);
                await udp.SendAsync(query.ToArray(), new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, Node.UDPSocket!.Value.Port.ToUInt16()), timeout.Token);

                response = (await udp.ReceiveAsync(timeout.Token)).Buffer;

            }

            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0, 2)), Is.EqualTo(0x1234), "the response's id");

            var flags     = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
            var answers   = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6, 2));

            // Past the question: the name as it was sent, then type and class.
            var offset    = 12 + (query.Count - 12);
            var addresses = new List<String>();

            for (var i = 0; i < answers; i++)
            {

                // The name: a pointer, or labels.
                if ((response[offset] & 0xC0) == 0xC0)
                    offset += 2;
                else
                {
                    while (response[offset] != 0)
                        offset += response[offset] + 1;
                    offset++;
                }

                var type      = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset,     2));
                var rdLength  = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 8, 2));

                offset += 10;

                if (type == 1 && rdLength == 4)
                    addresses.Add($"{response[offset]}.{response[offset + 1]}.{response[offset + 2]}.{response[offset + 3]}");

                offset += rdLength;

            }

            return (flags & 0x000F, (flags & 0x0400) != 0, [ .. addresses.Order() ]);

        }

        #endregion


        #region AddA_IsAnsweredAtOnce_OverUDPAndTCP_AndWritten()

        [Test]
        public async Task AddA_IsAnsweredAtOnce_OverUDPAndTCP_AndWritten()
        {

            var directory  = NewDirectory();
            var node       = await Started(directory);

            var before     = await Ask(node, "www.example.org");

            Assert.That(before.Addresses, Is.Empty, "no record yet");

            var said       = await AtTheConsole(node, "aRecords add www.example.org 192.0.2.10 10m");

            Assert.That(said[0], Does.Contain("www.example.org").And.Contain("192.0.2.10").And.Contain("600 s"));

            var overUDP    = await Ask(node, "www.example.org");
            var overTCP    = await Ask(node, "WWW.Example.ORG", OverTCP: true);

            Assert.Multiple(() => {
                Assert.That(overUDP.ResponseCode,   Is.EqualTo(0));
                Assert.That(overUDP.Authoritative,  Is.True);
                Assert.That(overUDP.Addresses,      Is.EqualTo(new[] { "192.0.2.10" }));
                Assert.That(overTCP.Addresses,      Is.EqualTo(new[] { "192.0.2.10" }), "names are compared without regard to case");
            });

            var written    = JObject.Parse(File.ReadAllText(ConfigFileIn(directory)));
            var lines      = written[DNSServerSettings.SectionName]?["records"]?.Values<String>().ToArray();

            Assert.That(lines,     Has.Length.EqualTo(1));
            Assert.That(lines![0], Does.Contain("www.example.org").And.Contain("600").And.Contain("192.0.2.10"));

            var listed     = await AtTheConsole(node, "aRecords");

            Assert.That(String.Join("\n", listed), Does.Contain("192.0.2.10"));

        }

        #endregion

        #region AddA_Twice_IsRefused_AndTheRRsetSharesItsTTL()

        [Test]
        public async Task AddA_Twice_IsRefused_AndTheRRsetSharesItsTTL()
        {

            var node   = await Started(NewDirectory());

            await AtTheConsole(node, "aRecords add host.example.org 192.0.2.1 1h");

            var again  = await AtTheConsole(node, "aRecords add host.example.org. 192.0.2.1");

            Assert.That(again[0], Does.Contain("already"));

            var second = await AtTheConsole(node, "aRecords add host.example.org 192.0.2.2 5m");

            Assert.That(second, Has.Length.EqualTo(2), "a note that the other record's TTL changed with it");

            var records = node.Records.All;

            Assert.That(records,                                      Has.Count.EqualTo(2));
            Assert.That(records.Select(record => record.TimeToLive),  Is.All.EqualTo(TimeSpan.FromMinutes(5)));

            var answer = await Ask(node, "host.example.org");

            Assert.That(answer.Addresses, Is.EqualTo(new[] { "192.0.2.1", "192.0.2.2" }));

        }

        #endregion

        #region RemoveA_OneAddress_ThenTheName()

        [Test]
        public async Task RemoveA_OneAddress_ThenTheName()
        {

            var directory = NewDirectory();
            var node      = await Started(directory);

            await AtTheConsole(node, "aRecords add host.example.org 192.0.2.1");
            await AtTheConsole(node, "aRecords add host.example.org 192.0.2.2");
            await AtTheConsole(node, "aRecords add other.example.org 192.0.2.3");

            var removedOne = await AtTheConsole(node, "aRecords remove host.example.org 192.0.2.1");

            Assert.That(removedOne[0], Does.Contain("no longer answers with 192.0.2.1"));
            Assert.That((await Ask(node, "host.example.org")).Addresses, Is.EqualTo(new[] { "192.0.2.2" }));

            var notThere   = await AtTheConsole(node, "aRecords remove host.example.org 192.0.2.99");

            Assert.That(notThere[0], Does.Contain("nothing was removed"));

            var removedAll = await AtTheConsole(node, "aRecords delete host.example.org");

            Assert.That(removedAll[0], Does.Contain("192.0.2.2"));

            var gone       = await Ask(node, "host.example.org");
            var other      = await Ask(node, "other.example.org");

            Assert.Multiple(() => {
                Assert.That(gone.Addresses,     Is.Empty);
                Assert.That(gone.ResponseCode,  Is.EqualTo(3), "NXDOMAIN: the name has no record left");
                Assert.That(other.Addresses,    Is.EqualTo(new[] { "192.0.2.3" }), "another name is left alone");
            });

            var lines      = JObject.Parse(File.ReadAllText(ConfigFileIn(directory)))[DNSServerSettings.SectionName]?["records"]?.Values<String>().ToArray();

            Assert.That(lines, Has.Length.EqualTo(1));
            Assert.That(lines![0], Does.Contain("other.example.org"));

        }

        #endregion

        #region Records_SurviveARestart()

        [Test]
        public async Task Records_SurviveARestart()
        {

            var directory = NewDirectory();
            var first     = await Started(directory);

            await AtTheConsole(first, "aRecords add a.example.org 192.0.2.1 2h");
            await AtTheConsole(first, "aRecords add b.example.org 198.51.100.7");

            await first.DisposeAsync();
            nodes.Remove(first);

            var second    = await Started(directory);

            var a         = await Ask(second, "a.example.org");
            var b         = await Ask(second, "b.example.org", OverTCP: true);

            Assert.Multiple(() => {
                Assert.That(a.Addresses,                                          Is.EqualTo(new[] { "192.0.2.1" }));
                Assert.That(b.Addresses,                                          Is.EqualTo(new[] { "198.51.100.7" }));
                Assert.That(second.Records.Named("a.example.org")[0].TimeToLive,  Is.EqualTo(TimeSpan.FromHours(2)));
            });

            var listed    = await AtTheConsole(second, "records");

            Assert.That(listed[0], Does.Contain("2 record(s)"));

        }

        #endregion

        #region ARecords_RefusesWhatIsNoAddressOrName()

        [Test]
        public async Task ARecords_RefusesWhatIsNoAddressOrName()
        {

            var node = await Started(NewDirectory());

            var noAddress  = await AtTheConsole(node, "aRecords add www.example.org 300.1.2.3");
            var noTTL      = await AtTheConsole(node, "aRecords add www.example.org 192.0.2.1 x");
            var noName     = await AtTheConsole(node, "aRecords add bad..name 192.0.2.1");
            var tooFew     = await AtTheConsole(node, "aRecords add www.example.org");

            Assert.Multiple(() => {
                Assert.That(noAddress[0],  Does.Contain("no IPv4 address"));
                Assert.That(noTTL[0],      Does.Contain("no TTL"));
                Assert.That(noName[0],     Does.Contain("no domain name"));
                Assert.That(tooFew[0],     Does.StartWith("aRecords add <name> <IPv4>"));
            });

            Assert.That(node.Records.Count, Is.Zero);

        }

        #endregion

        #region DNSServer_SaysHowItStands()

        [Test]
        public async Task DNSServer_SaysHowItStands()
        {

            var node   = await Started(NewDirectory());

            await AtTheConsole(node, "aRecords add www.example.org 192.0.2.10");
            await Ask(node, "www.example.org");

            var status = String.Join("\n", await AtTheConsole(node, "dnsServer"));

            Assert.Multiple(() => {
                Assert.That(status, Does.Contain("answering       yes"));
                Assert.That(status, Does.Contain("udp://127.0.0.1:"));
                Assert.That(status, Does.Contain("1 A"));
                Assert.That(status, Does.Match(@"answered\s+[1-9]\d* response"));
            });

        }

        #endregion

        #region ABrokenRecord_IsNoStart()

        [Test]
        public void ABrokenRecord_IsNoStart()
        {

            var directory = NewDirectory();

            File.WriteAllText(ConfigFileIn(directory),
                              new JObject(new JProperty(DNSServerSettings.SectionName, new JObject(
                                  new JProperty("records", new JArray("www.example.org. 600 IN A not-an-address"))
                              ))).ToString());

            var problem = Assert.Throws<InvalidOperationException>(() => new DNSServerNode(
                              HTTPPort:          TestPorts.Free(),
                              AccountsPath:      Path.Combine(directory, "accounts"),
                              ConfigFile:        new WWCPConfigFile(ConfigFileIn(directory)),
                              CertificatesPath:  Path.Combine(directory, "certificates"),
                              LogToConsole:      false,
                              BridgeDebugLog:    false
                          ));

            Assert.That(problem!.Message, Does.Contain("not-an-address"));

        }

        #endregion

    }

}
