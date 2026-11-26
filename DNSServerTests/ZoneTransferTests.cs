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

using System.Net;
using IPAddress = System.Net.IPAddress;
using System.Net.Sockets;
using System.Buffers.Binary;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.DNSServer.CommandLine;

#endregion

namespace cloud.charging.open.DNSServer.Tests
{

    /// <summary>
    /// Zone transfers to secondaries - AXFR and IXFR, by address and by TSIG
    /// key - and NOTIFY of a new serial: a DNS server on the loopback address,
    /// asked as a secondary asks it, and a stand-in secondary it tells.
    /// </summary>
    /// <remarks>
    /// Nothing here leaves this machine: every server asked is one started here.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public class ZoneTransferTests
    {

        #region Data

        private readonly List<DNSServerNode>  nodes        = [];
        private readonly List<String>         directories  = [];

        private const String Secret = "c2VjcmV0LW9mLW5zMi10cmFuc2Zlci0zMi1vY3RldHMh";

        private static readonly TSIGKey Key = TSIGKey.ParseBase64(DomainName.Parse("ns2-transfer."), Secret);

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


        #region (private) Started(Zone = null, Section = null)

        /// <summary>
        /// A DNS server on the loopback address with the zone example.org. - as
        /// it is given - and lab.example.org. within it, records in both and in
        /// none, and DNS on ports the system chose: started.
        /// </summary>
        private async Task<DNSServerNode> Started(JObject?  Zone     = null,
                                                  JObject?  Section  = null)
        {

            var directory = Path.Combine(Path.GetTempPath(), "DNSServerTests-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);
            directories.Add(directory);

            var zone = new JObject(new JProperty("name", "example.org."), new JProperty("serial", 2026112600));

            if (Zone is not null)
                zone.Merge(Zone);

            var section = new JObject(
                              new JProperty("listen",   "127.0.0.1"),
                              new JProperty("records",  new JArray(
                                  "www.example.org.        600 IN A   192.0.2.10",
                                  "ns.example.org.         600 IN A   192.0.2.53",
                                  "example.org.            600 IN MX  10 mail.example.org.",
                                  "ns.lab.example.org.     600 IN A   192.0.2.54",
                                  "host.lab.example.org.   600 IN A   192.0.2.55",
                                  "www.example.net.        600 IN A   198.51.100.10"
                              )),
                              new JProperty("zones",    new JArray(
                                  zone,
                                  new JObject(new JProperty("name",         "lab.example.org."),
                                              new JProperty("nameServers",  new JArray("ns.lab.example.org.")))
                              ))
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
                           SSH:               new SSHSettings(Enabled: false),
                           CommandLine:       settings => settings with { Port = IPPort.Zero }
                       );

            nodes.Add(node);

            await node.Start();

            return node;

        }

        #endregion

        #region (private static) Query(Type, Name, TheirSerial = null)

        /// <summary>
        /// A query as a secondary sends it: an AXFR, or an IXFR with the SOA of
        /// the serial it has in its authority section (RFC 1995 §3).
        /// </summary>
        private static Byte[] Query(DNSResourceRecordTypes  Type,
                                    String                  Name,
                                    UInt32?                 TheirSerial = null)
        {

            var query = new DNSPacket(
                            TransactionId:        0x4242,
                            QueryOrResponse:      DNSQueryResponse.Query,
                            Opcode:               0,
                            AuthoritativeAnswer:  false,
                            Truncation:           false,
                            RecursionDesired:     false,
                            RecursionAvailable:   false,
                            ResponseCode:         DNSResponseCodes.NoError,
                            Questions:            [ new DNSQuestion(DNSServiceName.Parse(Name), Type, DNSQueryClasses.IN) ],
                            AnswerRRs:            [],
                            AuthorityRRs:         TheirSerial is UInt32 serial
                                                      ? [ new SOA(DomainName.Parse(Name), DNSQueryClasses.IN, TimeSpan.Zero,
                                                                  DomainName.Parse("ns.example.org."),
                                                                  org.GraphDefined.Vanaheimr.Hermod.Mail.SimpleEMailAddress.Parse("hostmaster@example.org"),
                                                                  serial, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) ]
                                                      : [],
                            AdditionalRRs:        []
                        );

            using var stream = new MemoryStream();

            query.Serialize(stream);

            return stream.ToArray();

        }

        #endregion

        #region (private static) Send(Node, Message, OverTCP = true)

        /// <summary>
        /// A message sent to the server, and its answer as it came.
        /// </summary>
        private static async Task<Byte[]> Send(DNSServerNode  Node,
                                               Byte[]         Message,
                                               Boolean        OverTCP = true)
        {

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            if (!OverTCP)
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                await udp.SendAsync(Message, new IPEndPoint(IPAddress.Loopback, Node.UDPSocket!.Value.Port.ToUInt16()), timeout.Token);
                return (await udp.ReceiveAsync(timeout.Token)).Buffer;
            }

            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", Node.TCPSocket!.Value.Port.ToUInt16(), timeout.Token);

            var stream = tcp.GetStream();
            var length = new Byte[2];

            BinaryPrimitives.WriteUInt16BigEndian(length, (UInt16) Message.Length);
            await stream.WriteAsync(length,  timeout.Token);
            await stream.WriteAsync(Message, timeout.Token);

            await stream.ReadExactlyAsync(length, timeout.Token);
            var response = new Byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
            await stream.ReadExactlyAsync(response, timeout.Token);

            return response;

        }

        /// <summary>
        /// A message read back as a secondary reads it - its header, and its
        /// answer section with every record of its type - without the TSIG
        /// record that may sign it.
        /// </summary>
        private sealed record Message(Byte                       Opcode,
                                      Boolean                    AuthoritativeAnswer,
                                      Boolean                    Truncation,
                                      DNSResponseCodes           ResponseCode,
                                      DNSQuestion                Question,
                                      List<IDNSResourceRecord>   AnswerRRs);

        private static Message Parsed(Byte[] Answer)
        {

            var unsigned = TSIGSigner.TryStripTSIG(Answer, out var stripped, out _) && stripped is not null
                               ? stripped
                               : Answer;

            var header   = DNSPacket.Parse(IPSocket.Zero, IPSocket.Zero, new MemoryStream(unsigned));

            // Past the header and the one question - its name, type and class.
            var stream   = new MemoryStream(unsigned) { Position = 12 };
            DNSTools.ExtractName(stream);
            stream.Position += 4;

            var answers  = new List<IDNSResourceRecord>();

            for (var i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(unsigned.AsSpan(6, 2)); i++)
                answers.Add(DNSInfo.ReadResourceRecord(stream)!);

            return new Message(header.Opcode,
                               header.AuthoritativeAnswer,
                               header.Truncation,
                               header.ResponseCode,
                               header.Questions.Single(),
                               answers);

        }

        /// <summary>
        /// The records of an answer as a zone file writes them, without their TTL.
        /// </summary>
        private static String[] Lines(Message Answer)

            => [ .. Answer.AnswerRRs.Select(record => System.Text.RegularExpressions.Regex.Replace(
                                                          System.Text.RegularExpressions.Regex.Replace(record.ToZoneFileString().Trim(), @"\s+", " "),
                                                          @"^(\S+) \d+ ", "$1 ")) ];

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


        #region AXFR_CarriesTheZoneBetweenItsSOAs_WithWhatIsDelegated()

        /// <summary>
        /// RFC 5936 §2.2: the SOA, every record of the zone, the SOA again - and
        /// of the zone within it its NS record and the address of its name
        /// server (the glue), not its records; of a name in no zone, nothing.
        /// </summary>
        [Test]
        public async Task AXFR_CarriesTheZoneBetweenItsSOAs_WithWhatIsDelegated()
        {

            var node    = await Started(new JObject(new JProperty("transfer", new JArray("127.0.0.1"))));
            var answer  = Parsed(await Send(node, Query(DNSResourceRecordTypes.AXFR, "example.org.")));
            var lines   = Lines(answer);

            Assert.Multiple(() => {

                Assert.That(answer.ResponseCode,         Is.EqualTo(DNSResponseCodes.NoError));
                Assert.That(answer.AuthoritativeAnswer,  Is.True);

                Assert.That(answer.AnswerRRs.First(),    Is.InstanceOf<SOA>());
                Assert.That(answer.AnswerRRs.Last(),     Is.InstanceOf<SOA>());
                Assert.That(((SOA) answer.AnswerRRs.First()).Serial, Is.EqualTo(2026112600));

                Assert.That(lines, Does.Contain("example.org. IN NS ns.example.org."));
                Assert.That(lines, Does.Contain("www.example.org. IN A 192.0.2.10"));
                Assert.That(lines, Does.Contain("ns.example.org. IN A 192.0.2.53"));
                Assert.That(lines, Does.Contain("example.org. IN MX 10 mail.example.org."));
                Assert.That(lines, Does.Contain("lab.example.org. IN NS ns.lab.example.org."), "the delegation of the zone within it");
                Assert.That(lines, Does.Contain("ns.lab.example.org. IN A 192.0.2.54"),        "the glue of the zone within it");
                Assert.That(lines, Has.None.StartsWith("host.lab.example.org."),               "a record of the zone within it");
                Assert.That(lines, Has.None.StartsWith("www.example.net."),                    "a record of no zone");

                Assert.That(answer.AnswerRRs.Count,      Is.EqualTo(8));

            });

            var status = node.ZonesJSON().First(zone => zone["name"]!.Value<String>() == "example.org.")["lastTransfer"]!;

            Assert.Multiple(() => {
                Assert.That(status["kind"]!.Value<String>(),     Is.EqualTo("AXFR"));
                Assert.That(status["to"]!.Value<String>(),       Is.EqualTo("127.0.0.1"));
                Assert.That(status["records"]!.Value<Int32>(),   Is.EqualTo(8));
                Assert.That(status["refused"]!.Type,             Is.EqualTo(JTokenType.Null));
            });

            var lab = Parsed(await Send(node, Query(DNSResourceRecordTypes.AXFR, "lab.example.org.")));

            Assert.That(lab.ResponseCode, Is.EqualTo(DNSResponseCodes.Refused), "the zone within it has a transfer list of its own");

        }

        #endregion

        #region AXFR_IsRefused_WhereItIsNoOnesToAskFor()

        [Test]
        public async Task AXFR_IsRefused_WhereItIsNoOnesToAskFor()
        {

            var node      = await Started(new JObject(new JProperty("transfer", new JArray("192.0.2.0/24", "2001:db8::/32"))));
            var refused   = Parsed(await Send(node, Query(DNSResourceRecordTypes.AXFR, "example.org.")));
            var notAZone  = Parsed(await Send(node, Query(DNSResourceRecordTypes.AXFR, "example.net.")));

            Assert.Multiple(() => {
                Assert.That(refused. ResponseCode,  Is.EqualTo(DNSResponseCodes.Refused));
                Assert.That(refused. AnswerRRs,     Is.Empty);
                Assert.That(notAZone.ResponseCode,  Is.EqualTo(DNSResponseCodes.NotAuthorized), "NOTAUTH for a zone it does not have (RFC 5936 §2.2.1)");
            });

            Assert.That(node.Transfers.LastTransfers["example.org"].Refused, Does.Contain("neither its address nor a key"));

            var nobody   = await Started();
            var asked    = Parsed(await Send(nobody, Query(DNSResourceRecordTypes.AXFR, "example.org.")));

            Assert.That(asked.ResponseCode, Is.EqualTo(DNSResponseCodes.Refused), "a zone without a transfer list is transferred to nobody");
            Assert.That(nobody.Transfers.LastTransfers["example.org"].Refused, Is.EqualTo("the zone is transferred to nobody"));

        }

        #endregion

        #region IXFR_IsTheSOAAlone_WhereTheSecondaryIsUpToDate_AndTheWholeZoneOtherwise()

        /// <summary>
        /// RFC 1995 §4: up to date, the SOA alone; behind, the zone as an AXFR
        /// carries it; over UDP the SOA alone, so that it asks again over TCP.
        /// </summary>
        [Test]
        public async Task IXFR_IsTheSOAAlone_WhereTheSecondaryIsUpToDate_AndTheWholeZoneOtherwise()
        {

            var node      = await Started(new JObject(new JProperty("transfer", new JArray("127.0.0.1"))));

            var upToDate  = Parsed(await Send(node, Query(DNSResourceRecordTypes.IXFR, "example.org.", 2026112600)));
            var behind    = Parsed(await Send(node, Query(DNSResourceRecordTypes.IXFR, "example.org.", 2026112500)));
            var overUDP   = Parsed(await Send(node, Query(DNSResourceRecordTypes.IXFR, "example.org.", 2026112500), OverTCP: false));
            var axfrUDP   = Parsed(await Send(node, Query(DNSResourceRecordTypes.AXFR, "example.org."), OverTCP: false));

            // A serial that went round is still after (RFC 1982): 4000000000 + 2^31 is before 2026112600 no longer.
            var wentRound = Parsed(await Send(node, Query(DNSResourceRecordTypes.IXFR, "example.org.", 2026112600u + 0x7FFFFFFFu)));

            Assert.Multiple(() => {
                Assert.That(upToDate.AnswerRRs.Count,     Is.EqualTo(1));
                Assert.That(upToDate.AnswerRRs.Single(),  Is.InstanceOf<SOA>());
                Assert.That(behind.  AnswerRRs.Count,     Is.EqualTo(8));
                Assert.That(behind.  AnswerRRs.Last(),    Is.InstanceOf<SOA>());
                Assert.That(overUDP. AnswerRRs.Count,     Is.EqualTo(1));
                Assert.That(wentRound.AnswerRRs.Count,    Is.EqualTo(1), "a serial ahead of the zone's is up to date as well");
                Assert.That(axfrUDP. Truncation,          Is.True,  "an AXFR over UDP: truncated, to be asked for over TCP");
                Assert.That(axfrUDP. AnswerRRs,           Is.Empty);
            });

            Assert.Multiple(() => {
                Assert.That(ZoneTransfers.IsBefore(2026112500, 2026112600),   Is.True);
                Assert.That(ZoneTransfers.IsBefore(2026112600, 2026112500),   Is.False);
                Assert.That(ZoneTransfers.IsBefore(UInt32.MaxValue, 1),       Is.True, "a serial that went round");
                Assert.That(ZoneTransfers.IsBefore(1, 1),                     Is.False);
            });

        }

        #endregion

        #region AXFR_ByATSIGKey_IsSignedBack()

        /// <summary>
        /// A zone transferred to whoever signs with its key (RFC 8945), from
        /// whatever address - and to nobody else - its answer signed with the
        /// key in turn; the key's secret never said.
        /// </summary>
        [Test]
        public async Task AXFR_ByATSIGKey_IsSignedBack()
        {

            var node      = await Started(new JObject(new JProperty("transferKeys", new JArray("ns2-transfer"))),
                                          new JObject(new JProperty("tsigKeys", new JArray(
                                              new JObject(new JProperty("name",       "ns2-transfer."),
                                                          new JProperty("algorithm",  "hmac-sha256"),
                                                          new JProperty("secret",     Secret))))));

            var unsigned  = Parsed(await Send(node, Query(DNSResourceRecordTypes.AXFR, "example.org.")));

            var request   = TSIGSigner.Sign(Query(DNSResourceRecordTypes.AXFR, "example.org."), Key);
            var mac       = TSIGSigner.Verify(request, Key).MAC;
            var answer    = await Send(node, request);
            var verified  = TSIGSigner.Verify(answer, Key, RequestMAC: mac);

            Assert.Multiple(() => {
                Assert.That(unsigned.ResponseCode,          Is.EqualTo(DNSResponseCodes.Refused));
                Assert.That(Parsed(answer).ResponseCode,    Is.EqualTo(DNSResponseCodes.NoError));
                Assert.That(Parsed(answer).AnswerRRs.Count,   Is.EqualTo(8));
                Assert.That(verified.IsValid,               Is.True, verified.Description);
                Assert.That(node.Transfers.LastTransfers["example.org"].To, Is.EqualTo("127.0.0.1 (TSIG key ns2-transfer.)"));
            });

            var said = node.Settings.ToJSON().ToString() + node.ConfigurationJSON().ToString() + node.ZonesJSON().ToString();

            Assert.That(said, Does.Not.Contain(Secret), "the secret of a TSIG key was said");
            Assert.That(node.Settings.ToJSON()["tsigKeys"]![0]!["name"]!.Value<String>(), Is.EqualTo("ns2-transfer."));

        }

        #endregion

        #region NOTIFY_TellsTheSecondaries_AtTheStart_AndOfEachNewSerial()

        /// <summary>
        /// RFC 1996: a secondary told of the serial at the start (§3.10) and of
        /// each new one - opcode 4, AA, the question the SOA of the zone, and
        /// its SOA as the answer (§3.7) - and the answer of the secondary seen.
        /// </summary>
        [Test]
        public async Task NOTIFY_TellsTheSecondaries_AtTheStart_AndOfEachNewSerial()
        {

            using var secondary = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            var port  = ((IPEndPoint) secondary.Client.LocalEndPoint!).Port;
            var node  = await Started(new JObject(new JProperty("notify",   new JArray($"127.0.0.1:{port}")),
                                                  new JProperty("transfer", new JArray("127.0.0.1"))));

            async Task<Message> Told()
            {

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                var received = await secondary.ReceiveAsync(timeout.Token);
                var answer   = received.Buffer.ToArray();

                // Answered as a secondary does: the same message, QR set.
                answer[2] |= 0x80;
                await secondary.SendAsync(answer, received.RemoteEndPoint, timeout.Token);

                return Parsed(received.Buffer);

            }

            var atTheStart = await Told();

            Assert.Multiple(() => {
                Assert.That(atTheStart.Opcode,                                  Is.EqualTo(4));
                Assert.That(atTheStart.AuthoritativeAnswer,                     Is.True);
                Assert.That(atTheStart.Question.DomainName.FullName,            Is.EqualTo("example.org."));
                Assert.That(atTheStart.Question.QueryType,                      Is.EqualTo(DNSResourceRecordTypes.SOA));
                Assert.That(((SOA) atTheStart.AnswerRRs.Single()).Serial,       Is.EqualTo(2026112600));
            });

            await AtTheConsole(node, "aRecords add mail.example.org 192.0.2.25");

            var newSerial  = await Told();
            var serial     = node.Records.Zones.First(zone => zone.Apex == "example.org").Serial;

            Assert.That(((SOA) newSerial.AnswerRRs.Single()).Serial, Is.EqualTo(serial));
            Assert.That(serial,                                      Is.GreaterThan(2026112600));

            // A record of the zone within it is no change of this one.
            await AtTheConsole(node, "aRecords add other.lab.example.org 192.0.2.56");

            using (var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                Assert.That(async () => await secondary.ReceiveAsync(quiet.Token), Throws.InstanceOf<OperationCanceledException>(),
                            "told of a change of the zone within it");

            for (var i = 0; i < 50 && !(node.Notifier.LastNotifies.TryGetValue($"example.org 127.0.0.1:{port}", out var seen) && seen.Serial == serial && seen.Answered); i++)
                await Task.Delay(20);

            Assert.That(node.Notifier.LastNotifies[$"example.org 127.0.0.1:{port}"].Answered, Is.True);

            var listed = await AtTheConsole(node, "zones example.org");

            Assert.That(listed, Has.Some.Contains($"NOTIFY of serial {serial} to 127.0.0.1:{port}").And.Some.Contains("answered"));

        }

        #endregion

        #region NOTIFY_NotAnswered_IsSentAgain_AndSaidSo()

        [Test]
        public async Task NOTIFY_NotAnswered_IsSentAgain_AndSaidSo()
        {

            using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            var port     = ((IPEndPoint) silent.Client.LocalEndPoint!).Port;
            var notifier = new ZoneNotifier(TimeSpan.FromHours(1));
            var zone     = Zone.New(DomainName.Parse("example.org."), null, DateTimeOffset.UtcNow);
            var wait     = ZoneNotifier.FirstWait;

            ZoneNotifier.FirstWait = TimeSpan.FromMilliseconds(50);

            try
            {

                var answered = await notifier.NotifyAsync(zone, new IPEndPoint(IPAddress.Loopback, port));

                var received = 0;

                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    while (received < ZoneNotifier.MaxTries)
                    {
                        await silent.ReceiveAsync(timeout.Token);
                        received++;
                    }

                Assert.Multiple(() => {
                    Assert.That(answered,                                                         Is.False);
                    Assert.That(received,                                                         Is.EqualTo(ZoneNotifier.MaxTries));
                    Assert.That(notifier.LastNotifies[$"example.org 127.0.0.1:{port}"].Tries,     Is.EqualTo(ZoneNotifier.MaxTries));
                    Assert.That(notifier.LastNotifies[$"example.org 127.0.0.1:{port}"].Answered,  Is.False);
                });

            }
            finally
            {
                ZoneNotifier.FirstWait = wait;
            }

        }

        #endregion

        #region AtTheConsole_TheTransfersOfAZoneAreSet()

        [Test]
        public async Task AtTheConsole_TheTransfersOfAZoneAreSet()
        {

            var node      = await Started(Section: new JObject(new JProperty("tsigKeys", new JArray(
                                                       new JObject(new JProperty("name",    "ns2-transfer."),
                                                                   new JProperty("secret",  Secret))))));

            var serial    = node.Records.Zones.First().Serial;

            var transfer  = await AtTheConsole(node, "zones transfer example.org 192.0.2.53 2001:db8:53::/48");
            var keys      = await AtTheConsole(node, "zones transferKeys example.org ns2-transfer");
            var notify    = await AtTheConsole(node, "zones notify example.org 192.0.2.53 [2001:db8:53::1]:5353");
            var noKey     = await AtTheConsole(node, "zones transferKeys example.org other-key");
            var noAddress = await AtTheConsole(node, "zones transfer example.org secondary.example.org");
            var noZone    = await AtTheConsole(node, "zones notify example.com 192.0.2.53");
            var listed    = await AtTheConsole(node, "zones example.org");

            Assert.Multiple(() => {

                Assert.That(transfer,  Has.Some.EqualTo("The zone example.org. is transferred to 192.0.2.53, 2001:db8:53::/48, and tells nobody of a new serial."));
                Assert.That(keys,      Has.Some.StartWith("The zone example.org. is transferred to 192.0.2.53, 2001:db8:53::/48, TSIG key ns2-transfer."));
                Assert.That(notify,    Has.Some.EndWith("and tells 192.0.2.53, [2001:db8:53::1]:5353 of each new serial."));
                Assert.That(noKey,     Has.Some.StartWith("'other-key' is no TSIG key of this name server: it has ns2-transfer."));
                Assert.That(noAddress, Has.Some.StartWith("'secondary.example.org' is no address or prefix"));
                Assert.That(noZone,    Has.Some.StartWith("The name server is not authoritative for example.com."));
                Assert.That(listed,    Has.Some.Contains("transferred to 192.0.2.53, 2001:db8:53::/48, TSIG key ns2-transfer., tells 192.0.2.53, [2001:db8:53::1]:5353 of a new serial"));

            });

            Assert.That(node.Records.Zones.First().Serial, Is.EqualTo(serial), "the serial went on, though the zone holds what it held");

            var written = JObject.Parse(File.ReadAllText(node.ConfigFile.Path))[DNSServerSettings.SectionName]!;

            Assert.Multiple(() => {
                Assert.That(written["zones"]![0]!["transfer"]!.Values<String>(),      Is.EqualTo(new[] { "192.0.2.53", "2001:db8:53::/48" }));
                Assert.That(written["zones"]![0]!["transferKeys"]!.Values<String>(),  Is.EqualTo(new[] { "ns2-transfer." }));
                Assert.That(written["zones"]![0]!["notify"]!.Values<String>(),        Is.EqualTo(new[] { "192.0.2.53", "[2001:db8:53::1]:5353" }));
                Assert.That(written["tsigKeys"]![0]!["secret"]!.Value<String>(),      Is.EqualTo(Secret), "the secret written by hand is kept");
            });

            var none      = await AtTheConsole(node, "zones transfer example.org none");

            Assert.That(none, Has.Some.StartWith("The zone example.org. is transferred to TSIG key ns2-transfer."));

            var notifyOnly = await AtTheConsole(node, "zones transferKeys example.org none");

            Assert.That(notifyOnly, Has.Some.StartWith("It tells secondaries of a new serial that it transfers to nobody"));

        }

        #endregion

        #region TheSectionRefusesWhatCannotBe(Section, Says)

        [TestCase("""{ "zones": [ { "name": "example.org.", "transfer": [ "secondary.example.org" ] } ] }""",                     "is no address or prefix")]
        [TestCase("""{ "zones": [ { "name": "example.org.", "notify": [ "192.0.2.53:0" ] } ] }""",                               "is no address to tell")]
        [TestCase("""{ "zones": [ { "name": "example.org.", "transferKeys": [ "ns2-transfer." ] } ] }""",                        "not among the \"tsigKeys\"")]
        [TestCase("""{ "tsigKeys": [ { "name": "ns2-transfer.", "secret": "c2hvcnQ=" } ] }""",                                    "at least 16 octets")]
        [TestCase("""{ "tsigKeys": [ { "name": "ns2-transfer.", "algorithm": "hmac-md5", "secret": "c2VjcmV0LW9mLW5zMi10cmFuc2Zlcg==" } ] }""", "hmac-md5")]
        [TestCase("""{ "tsigKeys": [ { "name": "k.", "secret": "c2VjcmV0LW9mLW5zMi10cmFuc2Zlcg==" }, { "name": "K", "secret": "c2VjcmV0LW9mLW5zMi10cmFuc2Zlcg==" } ] }""", "listed twice")]
        public void TheSectionRefusesWhatCannotBe(String Section, String Says)
        {

            Assert.That(DNSServerSettings.TryParse(JObject.Parse(Section), out _, out var error), Is.False);
            Assert.That(error, Does.Contain(Says));

        }

        #endregion

    }

}
