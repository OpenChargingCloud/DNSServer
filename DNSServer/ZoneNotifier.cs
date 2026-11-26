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
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Collections.Concurrent;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// What the last NOTIFY of a zone to one secondary came to.
    /// </summary>
    /// <param name="At">When it was sent first.</param>
    /// <param name="To">The secondary.</param>
    /// <param name="Serial">The serial it told of.</param>
    /// <param name="Answered">Whether the secondary answered it.</param>
    /// <param name="Tries">How often it was sent.</param>
    public sealed record ZoneNotify(DateTimeOffset  At,
                                    String          To,
                                    UInt32          Serial,
                                    Boolean         Answered,
                                    Int32           Tries)
    {

        public JObject ToJSON()

            => new (
                   new JProperty("at",        At.ToString("o")),
                   new JProperty("to",        To),
                   new JProperty("serial",    Serial),
                   new JProperty("answered",  Answered),
                   new JProperty("tries",     Tries)
               );

    }


    /// <summary>
    /// NOTIFY (RFC 1996): the secondaries of a zone told that it has a new
    /// serial, so that they ask for it now rather than at their next refresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A NOTIFY is a message of the opcode 4, the question the SOA of the zone,
    /// and the SOA in its answer section as a hint (RFC 1996 §3.7). It is sent
    /// over UDP to each address of the "notify" of the zone, and sent again -
    /// after two, four and eight seconds - until the secondary answers it
    /// (§3.6), at most four times.
    /// </para>
    /// <para>
    /// Sent where a serial goes on, for a zone added with secondaries, for the
    /// secondaries a zone is given anew - and for every zone at the start
    /// (§3.10), which may have changed while the server did not run.
    /// </para>
    /// </remarks>
    /// <param name="TTL">The TTL of the SOA it carries.</param>
    public sealed class ZoneNotifier(TimeSpan TTL)
    {

        #region Data

        /// <summary>
        /// How often a NOTIFY is sent at most.
        /// </summary>
        public const Int32 MaxTries = 4;

        /// <summary>
        /// How long the first send waits for an answer; every next one twice as long.
        /// </summary>
        public static TimeSpan FirstWait { get; set; } = TimeSpan.FromSeconds(2);

        private readonly ConcurrentDictionary<String, ZoneNotify>  lastNotifies  = new (StringComparer.Ordinal);
        private readonly CancellationTokenSource                   stopping      = new ();

        #endregion

        #region Properties

        /// <summary>
        /// The last NOTIFY of each zone to each secondary, by the zone's name -
        /// normalized - a space, and the secondary.
        /// </summary>
        public IReadOnlyDictionary<String, ZoneNotify> LastNotifies
            => lastNotifies;

        #endregion

        #region Events

        /// <summary>
        /// A NOTIFY was answered, or was not: what the log is to say of it.
        /// </summary>
        public event Action<LogLevel, String>? OnNotice;

        #endregion


        #region Changed(Before, After)

        /// <summary>
        /// The secondaries of every zone whose serial went on, which is new, or
        /// which has secondaries it did not have, told - in the background.
        /// </summary>
        public void Changed(IReadOnlyList<Zone>  Before,
                            IReadOnlyList<Zone>  After)
        {

            foreach (var zone in After)
            {

                var before = Before.FirstOrDefault(other => other.Apex == zone.Apex);

                var targets = before is null || before.Serial != zone.Serial
                                  ? zone.Notify
                                  : [ .. zone.Notify.Where(endPoint => !before.Notify.Contains(endPoint)) ];

                foreach (var target in targets)
                    _ = NotifyAsync(zone, target);

            }

        }

        #endregion

        #region All(Zones)

        /// <summary>
        /// The secondaries of every zone told - at the start.
        /// </summary>
        public void All(IEnumerable<Zone> Zones)
        {
            foreach (var zone in Zones)
                foreach (var target in zone.Notify)
                    _ = NotifyAsync(zone, target);
        }

        #endregion

        #region NotifyAsync(Zone, Target)

        /// <summary>
        /// One secondary told of the serial of one zone: sent, and sent again
        /// until it answers - whether it did.
        /// </summary>
        public async Task<Boolean> NotifyAsync(Zone        Zone,
                                               IPEndPoint  Target)
        {

            var to       = Zone.TextOf(Target);
            var id       = (UInt16) Random.Shared.Next(0, UInt16.MaxValue + 1);
            var message  = Message(Zone, id);
            var key      = $"{Zone.Apex} {to}";
            var at       = DateTimeOffset.UtcNow;
            var wait     = FirstWait;

            try
            {

                using var udp = new UdpClient(Target.AddressFamily);

                for (var tries = 1; tries <= MaxTries; tries++)
                {

                    await udp.SendAsync(message, Target, stopping.Token).ConfigureAwait(false);

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(wait);

                    try
                    {

                        while (true)
                        {

                            var answer = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);

                            // The answer to this NOTIFY: its id, QR set, the opcode 4.
                            if (answer.Buffer.Length >= 12                                                   &&
                                BinaryPrimitives.ReadUInt16BigEndian(answer.Buffer.AsSpan(0, 2)) == id       &&
                                (answer.Buffer[2] & 0x80) != 0                                               &&
                                ((answer.Buffer[2] >> 3) & 0x0F) == 4)
                            {

                                lastNotifies[key] = new ZoneNotify(at, to, Zone.Serial, true, tries);

                                Say(LogLevel.Info, $"NOTIFY of {Zone.Name.FullName}, serial {Zone.Serial}, answered by {to}" +
                                                   (tries > 1 ? $" at the {tries}. try." : "."));

                                return true;

                            }

                        }

                    }
                    catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                    {
                        wait *= 2;
                    }
                    catch (SocketException)
                    {
                        // ICMP said nobody listens there - as good as no answer.
                        await Task.Delay(wait, stopping.Token).ConfigureAwait(false);
                        wait *= 2;
                    }

                }

                lastNotifies[key] = new ZoneNotify(at, to, Zone.Serial, false, MaxTries);

                Say(LogLevel.Warning, $"NOTIFY of {Zone.Name.FullName}, serial {Zone.Serial}, not answered by {to} after {MaxTries} tries: " +
                                      "it asks for the zone at its next refresh, or not at all where it does not know of it.");

                return false;

            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e)
            {

                lastNotifies[key] = new ZoneNotify(at, to, Zone.Serial, false, 0);

                Say(LogLevel.Warning, $"NOTIFY of {Zone.Name.FullName} could not be sent to {to}: {e.Message}");

                return false;

            }

        }

        #endregion

        #region (static) Message(Zone, Id)

        /// <summary>
        /// A NOTIFY of the zone as it goes on the wire (RFC 1996 §3.7): the
        /// opcode 4, AA set, the question its SOA, and its SOA as the answer.
        /// </summary>
        public Byte[] Message(Zone    Zone,
                              UInt16  Id)
        {

            var notify = new DNSPacket(
                             TransactionId:        Id,
                             QueryOrResponse:      DNSQueryResponse.Query,
                             Opcode:               4,
                             AuthoritativeAnswer:  true,
                             Truncation:           false,
                             RecursionDesired:     false,
                             RecursionAvailable:   false,
                             ResponseCode:         DNSResponseCodes.NoError,
                             Questions:            [ new DNSQuestion(DNSServiceName.Parse(Zone.Name.FullName), DNSResourceRecordTypes.SOA, DNSQueryClasses.IN) ],
                             AnswerRRs:            [ Zone.SOA(TTL) ],
                             AuthorityRRs:         [],
                             AdditionalRRs:        []
                         );

            using var stream = new MemoryStream();

            notify.Serialize(stream);

            return stream.ToArray();

        }

        #endregion

        #region Stop()

        /// <summary>
        /// Every NOTIFY not answered yet given up.
        /// </summary>
        public void Stop()
            => stopping.Cancel();

        #endregion

        #region (private) Say(Level, Text)

        private void Say(LogLevel Level, String Text)
            => OnNotice?.Invoke(Level, Text);

        #endregion

    }

}
