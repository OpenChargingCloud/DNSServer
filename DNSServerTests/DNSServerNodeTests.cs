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

            var (responseCode, authoritative, answers) = await AskFor(Node, Name, 1, OverTCP);

            return (responseCode,
                    authoritative,
                    [ .. answers.Where(answer => answer.Type == 1).Select(answer => answer.Data).Order() ]);

        }

        #endregion

        #region (private static) AskFor(Node, Name, Type, OverTCP = false)

        /// <summary>
        /// A query of the given type for the given name, sent as any resolver
        /// would, and the response code and every record of the answer: its
        /// type, and its data as a zone file says it - the address, the text of
        /// a TXT record with its character-strings put together, the name a
        /// CNAME points to.
        /// </summary>
        private static async Task<(Int32 ResponseCode, Boolean Authoritative, (UInt16 Type, String Data)[] Answers)> AskFor(DNSServerNode  Node,
                                                                                                                              String         Name,
                                                                                                                              UInt16         Type,
                                                                                                                              Boolean        OverTCP = false)
        {

            var query = new List<Byte> { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

            foreach (var label in Name.TrimEnd('.').Split('.'))
            {
                query.Add((Byte) label.Length);
                query.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
            }

            query.AddRange([ 0x00, (Byte) (Type >> 8), (Byte) Type, 0x00, 0x01 ]);

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
            var count     = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6, 2));

            // Past the question: the name as it was sent, then type and class.
            var offset    = query.Count;
            var answers   = new List<(UInt16, String)>();

            for (var i = 0; i < count; i++)
            {

                NameAt(response, ref offset);

                var type      = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset,     2));
                var rdLength  = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 8, 2));

                offset += 10;

                var rdata     = offset;

                answers.Add((type, type switch {
                    1  when rdLength ==  4  => new System.Net.IPAddress(response.AsSpan(offset, 4)). ToString(),
                    28 when rdLength == 16  => new System.Net.IPAddress(response.AsSpan(offset, 16)).ToString(),
                    5                       => NameAt(response, ref rdata),
                    16                      => TextAt(response, offset, rdLength),
                    _                       => Convert.ToHexString(response, offset, rdLength)
                }));

                offset += rdLength;

            }

            return (flags & 0x000F, (flags & 0x0400) != 0, [ .. answers ]);

        }

        /// <summary>
        /// The name at the given offset - labels, a pointer, or labels and then a
        /// pointer - with its trailing dot; the offset moved past it.
        /// </summary>
        private static String NameAt(Byte[] Message, ref Int32 Offset)
        {

            var labels  = new List<String>();
            var at      = Offset;
            var jumped  = false;

            while (Message[at] != 0)
            {

                if ((Message[at] & 0xC0) == 0xC0)
                {

                    if (!jumped)
                        Offset = at + 2;

                    at      = ((Message[at] & 0x3F) << 8) | Message[at + 1];
                    jumped  = true;
                    continue;

                }

                labels.Add(System.Text.Encoding.ASCII.GetString(Message, at + 1, Message[at]));
                at += Message[at] + 1;

            }

            if (!jumped)
                Offset = at + 1;

            return String.Join('.', labels) + ".";

        }

        /// <summary>
        /// The character-strings of a TXT record, put together (RFC 7208 §3.3).
        /// </summary>
        private static String TextAt(Byte[] Message, Int32 Offset, Int32 Length)
        {

            var bytes = new List<Byte>();
            var end   = Offset + Length;

            while (Offset < end)
            {
                bytes.AddRange(Message.AsSpan(Offset + 1, Message[Offset]));
                Offset += Message[Offset] + 1;
            }

            return System.Text.Encoding.UTF8.GetString([ .. bytes ]);

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

        #region AAAA_TXT_AndCNAME_AreAnsweredAtOnce_AndSurviveARestart()

        [Test]
        public async Task AAAA_TXT_AndCNAME_AreAnsweredAtOnce_AndSurviveARestart()
        {

            var directory  = NewDirectory();
            var first      = await Started(directory);

            var aaaa       = await AtTheConsole(first, "aaaaRecords add www.example.org 2001:db8::10 10m");
            var txt        = await AtTheConsole(first, "txtRecords add example.org \"v=spf1 ip6:2001:db8::/32 -all\"");
            var cname      = await AtTheConsole(first, "cnameRecords add docs.example.org www.example.org");

            Assert.Multiple(() => {
                Assert.That(aaaa [0], Does.Contain("www.example.org. answers with 2001:db8::10").And.Contain("600 s"));
                Assert.That(txt  [0], Does.Contain("example.org. answers with the text \"v=spf1 ip6:2001:db8::/32 -all\""));
                Assert.That(cname[0], Does.Contain("docs.example.org. is an alias of www.example.org."));
            });

            async Task AnswersAsAdded(DNSServerNode node)
            {

                var askedAAAA   = await AskFor(node, "www.example.org",  28);
                var askedTXT    = await AskFor(node, "example.org",      16, OverTCP: true);
                var askedCNAME  = await AskFor(node, "docs.example.org", 5);

                Assert.Multiple(() => {
                    Assert.That(askedAAAA. Answers, Is.EqualTo(new[] { ((UInt16) 28, "2001:db8::10") }));
                    Assert.That(askedTXT.  Answers, Is.EqualTo(new[] { ((UInt16) 16, "v=spf1 ip6:2001:db8::/32 -all") }));
                    Assert.That(askedCNAME.Answers, Is.EqualTo(new[] { ((UInt16)  5, "www.example.org.") }));
                    Assert.That(node.Records.Named("www.example.org")[0].TimeToLive, Is.EqualTo(TimeSpan.FromMinutes(10)));
                });

            }

            await AnswersAsAdded(first);
            await AnswersAsAdded(await Restarted(first, directory));

            var lines      = JObject.Parse(File.ReadAllText(ConfigFileIn(directory)))[DNSServerSettings.SectionName]?["records"]?.Values<String>().ToArray();

            Assert.That(lines, Has.Length.EqualTo(3));
            Assert.That(lines![1], Does.Contain("TXT").And.Contain("\"v=spf1 ip6:2001:db8::/32 -all\""));

        }

        #endregion

        #region ACNAME_StandsAlone()

        [Test]
        public async Task ACNAME_StandsAlone()
        {

            var node = await Started(NewDirectory());

            await AtTheConsole(node, "aRecords add www.example.org 192.0.2.10");
            await AtTheConsole(node, "cnameRecords add docs.example.org www.example.org");

            var besideA       = await AtTheConsole(node, "cnameRecords add www.example.org other.example.org");
            var besideCNAME   = await AtTheConsole(node, "txtRecords add docs.example.org \"some text\"");
            var secondCNAME   = await AtTheConsole(node, "cnameRecords add docs.example.org other.example.org");
            var itself        = await AtTheConsole(node, "cnameRecords add self.example.org SELF.example.org.");

            Assert.Multiple(() => {
                Assert.That(besideA    [0], Does.Contain("has records already (A)"));
                Assert.That(besideCNAME[0], Does.Contain("is an alias of www.example.org.").And.Contain("no other record"));
                Assert.That(secondCNAME[0], Does.Contain("one CNAME only"));
                Assert.That(itself     [0], Does.Contain("cannot be an alias of itself"));
            });

            Assert.That(node.Records.Count, Is.EqualTo(2));

            var removed       = await AtTheConsole(node, "cnameRecords remove docs.example.org");

            Assert.That(removed[0], Does.Contain("docs.example.org. is no longer an alias of www.example.org."));

            var nowItCan      = await AtTheConsole(node, "txtRecords add docs.example.org \"some text\"");

            Assert.That(nowItCan[0], Does.Contain("answers with the text \"some text\""));

        }

        #endregion

        #region AAAA_AndTXT_AreRemoved_OneByOne_OrAllOfAName()

        [Test]
        public async Task AAAA_AndTXT_AreRemoved_OneByOne_OrAllOfAName()
        {

            var node = await Started(NewDirectory());

            await AtTheConsole(node, "aaaaRecords add host.example.org 2001:db8::1");
            await AtTheConsole(node, "aaaaRecords add host.example.org 2001:db8::2");
            await AtTheConsole(node, "txtRecords add host.example.org first");
            await AtTheConsole(node, "txtRecords add host.example.org \"the second\"");

            // The same address, written otherwise, is the same address.
            var removedOne   = await AtTheConsole(node, "aaaaRecords remove host.example.org 2001:0db8:0:0:0:0:0:1");
            var removedText  = await AtTheConsole(node, "txtRecords remove host.example.org \"the second\"");
            var notThere     = await AtTheConsole(node, "txtRecords remove host.example.org \"the third\"");

            Assert.Multiple(() => {
                Assert.That(removedOne [0], Does.Contain("no longer answers with 2001:db8::1"));
                Assert.That(removedText[0], Does.Contain("no longer answers with the text \"the second\""));
                Assert.That(notThere   [0], Does.Contain("nothing was removed"));
            });

            var aaaa         = await AskFor(node, "host.example.org", 28);
            var txt          = await AskFor(node, "host.example.org", 16);

            Assert.Multiple(() => {
                Assert.That(aaaa.Answers.Select(answer => answer.Data), Is.EqualTo(new[] { "2001:db8::2" }));
                Assert.That(txt. Answers.Select(answer => answer.Data), Is.EqualTo(new[] { "first" }));
            });

            var removedAll   = await AtTheConsole(node, "aaaaRecords delete host.example.org");
            var listed       = await AtTheConsole(node, "aaaaRecords");
            var texts        = await AtTheConsole(node, "txtRecords host.example.org");

            Assert.Multiple(() => {
                Assert.That(removedAll[0], Does.Contain("2001:db8::2"));
                Assert.That(listed    [0], Does.StartWith("There is no AAAA record. 'aaaaRecords add <name> <IPv6>' adds one."));
                Assert.That(texts     [0], Does.StartWith("1 TXT record(s)"));
                Assert.That(texts     [1], Does.Contain("\"first\""));
            });

        }

        #endregion

        #region TheOtherTypes_RefuseWhatTheyCannotTake()

        [Test]
        public async Task TheOtherTypes_RefuseWhatTheyCannotTake()
        {

            var node = await Started(NewDirectory());

            var ipv4InAAAA   = await AtTheConsole(node, "aaaaRecords add www.example.org 192.0.2.1");
            var noIPv6       = await AtTheConsole(node, "aaaaRecords add www.example.org 2001:db8::zz");
            var noText       = await AtTheConsole(node, "txtRecords add www.example.org \"\"");
            var noTarget     = await AtTheConsole(node, "cnameRecords add docs.example.org bad..name");
            var sameText     = await AtTheConsole(node, "txtRecords add example.org hello");
            var again        = await AtTheConsole(node, "txtRecords add example.org. hello");
            var tooFew       = await AtTheConsole(node, "txtRecords add example.org");

            Assert.Multiple(() => {
                Assert.That(ipv4InAAAA[0],  Does.Contain("no IPv6 address"));
                Assert.That(noIPv6    [0],  Does.Contain("no IPv6 address"));
                Assert.That(noText    [0],  Does.Contain("needs a text"));
                Assert.That(noTarget  [0],  Does.Contain("no domain name"));
                Assert.That(sameText  [0],  Does.Contain("answers with the text \"hello\" from now on"));
                Assert.That(again     [0],  Does.Contain("already"));
                Assert.That(tooFew    [0],  Does.StartWith("txtRecords add <name> \"<text>\""));
            });

            Assert.That(node.Records.Count, Is.EqualTo(1));

        }

        #endregion

        #region ALongOrUnicodeText_IsAnsweredAsItIs()

        [Test]
        public async Task ALongOrUnicodeText_IsAnsweredAsItIs()
        {

            var directory  = NewDirectory();
            var first      = await Started(directory);

            // Longer than one character-string of 255 octets: answered in two.
            var long_      = String.Concat(Enumerable.Repeat("0123456789", 30));

            var addedLong  = await AtTheConsole(first, $"txtRecords add long.example.org {long_}");
            var addedUTF8  = await AtTheConsole(first, "txtRecords add utf8.example.org \"Grüße aus Jena\"");

            Assert.Multiple(() => {
                Assert.That(addedLong[0], Does.Contain("from now on"));
                Assert.That(addedUTF8[0], Does.Contain("from now on"));
            });

            async Task AnswersAsItIs(DNSServerNode node)
            {

                var askedLong  = await AskFor(node, "long.example.org", 16, OverTCP: true);
                var askedUTF8  = await AskFor(node, "utf8.example.org", 16);

                Assert.Multiple(() => {
                    Assert.That(askedLong.Answers.Select(answer => answer.Data), Is.EqualTo(new[] { long_ }));
                    Assert.That(askedUTF8.Answers.Select(answer => answer.Data), Is.EqualTo(new[] { "Grüße aus Jena" }));
                });

            }

            await AnswersAsItIs(first);
            await AnswersAsItIs(await Restarted(first, directory));

        }

        #endregion

        #region ATextWithQuotes_IsTypedWithBackslashes()

        [Test]
        public async Task ATextWithQuotes_IsTypedWithBackslashes()
        {

            var node     = await Started(NewDirectory());

            var added    = await AtTheConsole(node, "txtRecords add example.org \"say \\\"hello\\\" C:\\\\temp\"");

            Assert.That(added[0], Does.Contain("answers with the text \"say \"hello\" C:\\temp\""));

            var asked    = await AskFor(node, "example.org", 16);

            Assert.That(asked.Answers.Select(answer => answer.Data), Is.EqualTo(new[] { "say \"hello\" C:\\temp" }));

            var removed  = await AtTheConsole(node, "txtRecords remove example.org \"say \\\"hello\\\" C:\\\\temp\"");

            Assert.That(removed[0],         Does.Contain("no longer answers with"));
            Assert.That(node.Records.Count, Is.Zero);

        }

        #endregion

        #region (private) Restarted(Node, Directory)

        /// <summary>
        /// The node stopped, and another started from the same directory: what
        /// it answers with is what was written.
        /// </summary>
        private async Task<DNSServerNode> Restarted(DNSServerNode  Node,
                                                    String         Directory)
        {

            await Node.DisposeAsync();
            nodes.Remove(Node);

            return await Started(Directory);

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
