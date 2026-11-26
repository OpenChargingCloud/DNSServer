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

using System.Collections.Concurrent;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.DNSServer
{

    /// <summary>
    /// One zone transfer, or one refused: when, to whom, which kind, and of
    /// which serial - or why not.
    /// </summary>
    /// <param name="At">When it was asked for.</param>
    /// <param name="To">Who asked: its address, and its TSIG key where it signed with one.</param>
    /// <param name="Kind">"AXFR" or "IXFR".</param>
    /// <param name="Serial">The serial it was transferred with.</param>
    /// <param name="Records">How many records it carried, both SOA records included; 1 where the secondary was up to date.</param>
    /// <param name="Refused">Why it was not transferred, or null where it was.</param>
    public sealed record ZoneTransfer(DateTimeOffset  At,
                                      String          To,
                                      String          Kind,
                                      UInt32          Serial,
                                      Int32           Records,
                                      String?         Refused)
    {

        public JObject ToJSON()

            => new (
                   new JProperty("at",       At.ToString("o")),
                   new JProperty("to",       To),
                   new JProperty("kind",     Kind),
                   new JProperty("serial",   Serial),
                   new JProperty("records",  Records),
                   new JProperty("refused",  Refused)
               );

    }


    /// <summary>
    /// Zone transfers: AXFR (RFC 5936) and IXFR (RFC 1995) of the zones the
    /// name server is authoritative for, to their secondaries - and every
    /// other question to the request handler behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One message, over a stream.</b> A transfer is the SOA of the zone, its
    /// records, and its SOA again (RFC 5936 §2.2), in one message - a zone of a
    /// local network fits, and one that does not is answered SERVFAIL and said
    /// in the log. It is sent over TCP, TLS or a channel of DNS-over-SSH; over
    /// UDP, which RFC 5936 §4.2 leaves undefined, an AXFR is answered truncated,
    /// so that the client asks again over TCP.
    /// </para>
    /// <para>
    /// <b>IXFR as a whole.</b> This server keeps no history of a zone: an IXFR
    /// from a secondary that is up to date is answered with the SOA alone, and
    /// one from a secondary that is not with the whole zone, as an AXFR would be
    /// - both as RFC 1995 §4 allows. Over UDP it is answered with the SOA alone,
    /// and the secondary asks again over TCP where it is behind (RFC 1995 §2).
    /// </para>
    /// <para>
    /// <b>To whom.</b> A zone is transferred to the addresses and prefixes of its
    /// "transfer", and to a request signed with one of its "transferKeys" (RFC
    /// 8945) - to nobody else: REFUSED. A name the server has no zone of is
    /// answered NOTAUTH (RFC 5936 §2.2.1).
    /// </para>
    /// </remarks>
    /// <param name="Records">The records and zones of the name server.</param>
    /// <param name="Inner">What answers every other question.</param>
    public sealed class ZoneTransfers(ResourceRecords     Records,
                                      IDNSRequestHandler  Inner) : IDNSRequestHandler
    {

        #region Data

        /// <summary>
        /// The largest transfer in one message: the 65535 octets a stream frames
        /// one in, less room for the TSIG record that may sign it.
        /// </summary>
        public const Int32 MaxMessage = UInt16.MaxValue - 512;

        private readonly ConcurrentDictionary<String, ZoneTransfer> lastTransfers = new (StringComparer.Ordinal);

        #endregion

        #region Properties

        /// <summary>
        /// The last transfer of each zone, or the last refused - by its name, normalized.
        /// </summary>
        public IReadOnlyDictionary<String, ZoneTransfer> LastTransfers
            => lastTransfers;

        #endregion

        #region Events

        /// <summary>
        /// A transfer was made or refused: what the log is to say of it.
        /// </summary>
        public event Action<LogLevel, String>? OnNotice;

        #endregion


        #region ProcessDNSRequest(Request, CancellationToken)

        public Task<DNSResponse?> ProcessDNSRequest(DNSPacket          Request,
                                                    CancellationToken  CancellationToken = default)
        {

            if (Request.QueryOrResponse   != DNSQueryResponse.Query ||
                Request.Opcode            != 0                      ||
                Request.Questions.Count() != 1                      ||
                Request.Questions.First().QueryType is not (DNSResourceRecordTypes.AXFR or DNSResourceRecordTypes.IXFR))
                return Inner.ProcessDNSRequest(Request, CancellationToken);

            return Task.FromResult<DNSResponse?>(Transfer(Request, Request.Questions.First()));

        }

        #endregion

        #region (private) Transfer(Request, Question)

        private DNSResponse Transfer(DNSPacket    Request,
                                     DNSQuestion  Question)
        {

            var kind     = Question.QueryType == DNSResourceRecordTypes.AXFR ? "AXFR" : "IXFR";
            var name     = Question.DomainName.FullName;
            var address  = Request.RemoteSocket.IPAddress.ToDotNet();
            var who      = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString() +
                           (Request.TSIGKeyName    is DomainName key   ? $" (TSIG key {key.FullName})"   : "") +
                           (Request.ClientIdentity is String     login ? $" (over SSH as \"{login}\")"   : "");

            DNSResponse Answer(DNSResponseCodes                 ResponseCode,
                               IEnumerable<IDNSResourceRecord>  Answers,
                               Boolean                          Truncated = false)

                => Request.CreateResponse(
                       Opcode:               0,
                       AuthoritativeAnswer:  ResponseCode == DNSResponseCodes.NoError,
                       Truncation:           Truncated,
                       RecursionDesired:     Request.RecursionDesired,
                       RecursionAvailable:   false,
                       ResponseCode:         ResponseCode,
                       AnswerRRs:            Answers,
                       AuthorityRRs:         [],
                       AdditionalRRs:        []
                   );

            if (Question.QueryClass != DNSQueryClasses.IN ||
                Records.TransferOf(name) is not var (zone, records))
            {
                Say(LogLevel.Info, $"{kind} of {name} for {who} answered NOTAUTH: the name server has no zone of that name.");
                return Answer(DNSResponseCodes.NotAuthorized, []);
            }

            var apex = zone.Apex;

            if (!zone.MayTransferTo(address, Request.TSIGKeyName))
            {

                var why = zone.Transfer.Count == 0 && zone.TransferKeys.Count == 0
                              ? "the zone is transferred to nobody"
                              : "neither its address nor a key it signed with is one the zone is transferred to";

                lastTransfers[apex] = new ZoneTransfer(DateTimeOffset.UtcNow, who, kind, zone.Serial, 0, why);

                Say(LogLevel.Warning, $"{kind} of {zone.Name.FullName} refused to {who}: {why}.");

                return Answer(DNSResponseCodes.Refused, []);

            }

            var soa = records[0];

            #region IXFR: the SOA alone, where the secondary is up to date or asks over UDP

            if (kind == "IXFR")
            {

                var theirs = SerialOf(Request.AuthorityRRs.FirstOrDefault(record => record.Type == DNSResourceRecordTypes.SOA));

                if (!Request.OverStream || (theirs is UInt32 serial && !IsBefore(serial, zone.Serial)))
                {

                    lastTransfers[apex] = new ZoneTransfer(DateTimeOffset.UtcNow, who, kind, zone.Serial, 1, null);

                    Say(LogLevel.Info, Request.OverStream
                                           ? $"IXFR of {zone.Name.FullName} to {who}: up to date with serial {zone.Serial}."
                                           : $"IXFR of {zone.Name.FullName} to {who} over UDP: its SOA, serial {zone.Serial}.");

                    return Answer(DNSResponseCodes.NoError, [ soa ]);

                }

            }

            #endregion

            #region AXFR over UDP: truncated, so that it is asked for over TCP

            else if (!Request.OverStream)
            {
                Say(LogLevel.Info, $"AXFR of {zone.Name.FullName} for {who} over UDP answered truncated: a transfer is made over TCP.");
                return Answer(DNSResponseCodes.NoError, [], Truncated: true);
            }

            #endregion

            var response = Answer(DNSResponseCodes.NoError, records);

            using (var size = new MemoryStream())
            {

                response.Serialize(size);

                if (size.Length > MaxMessage)
                {

                    var why = $"its {records.Count} records take {size.Length} octets, and this server sends a transfer in one message of at most {MaxMessage}";

                    lastTransfers[apex] = new ZoneTransfer(DateTimeOffset.UtcNow, who, kind, zone.Serial, 0, why);

                    Say(LogLevel.Error, $"{kind} of {zone.Name.FullName} to {who} failed: {why}.");

                    return Answer(DNSResponseCodes.ServerFailure, []);

                }

            }

            lastTransfers[apex] = new ZoneTransfer(DateTimeOffset.UtcNow, who, kind, zone.Serial, records.Count, null);

            Say(LogLevel.Notice, $"{kind} of {zone.Name.FullName} to {who}: serial {zone.Serial}, {records.Count} records.");

            return response;

        }

        #endregion

        #region (static) SerialOf(Record)

        /// <summary>
        /// The serial of the SOA a secondary sends with its IXFR (RFC 1995 §3) -
        /// read as an SOA, or from the RDATA of the record Hermod keeps opaque in
        /// a request (RFC 3597): the first four of its last twenty octets, after
        /// the two names, which may be compressed and are not needed.
        /// </summary>
        public static UInt32? SerialOf(IDNSResourceRecord? Record)

            => Record switch {
                   SOA           soa                                    => soa.Serial,
                   UnknownRecord unknown when unknown.RData.Length >= 22 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(unknown.RData.AsSpan(unknown.RData.Length - 20, 4)),
                   _                                                    => null
               };

        #endregion

        #region (static) IsBefore(Serial1, Serial2)

        /// <summary>
        /// Whether the one serial is before the other, as serial number
        /// arithmetic says it (RFC 1982 §3.2): one that went round is still after.
        /// </summary>
        public static Boolean IsBefore(UInt32 Serial1, UInt32 Serial2)

            => Serial1 != Serial2 &&
               (Serial1 < Serial2
                    ? Serial2 - Serial1 < 0x80000000u
                    : Serial1 - Serial2 > 0x80000000u);

        #endregion

        #region (private) Say(Level, Text)

        private void Say(LogLevel Level, String Text)
            => OnNotice?.Invoke(Level, Text);

        #endregion

    }

}
