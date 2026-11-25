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

using org.GraphDefined.Vanaheimr.Hermod.DNS;

#endregion

namespace cloud.charging.open.DNSServer.CommandLine
{

    /// <summary>
    /// The TXT records of the name server: the texts a name answers with - SPF, DMARC, the proof of who holds a domain.
    /// </summary>
    /// <remarks>
    /// <code>
    /// txtRecords                                 every TXT record
    /// txtRecords &lt;name&gt;                          the TXT records of one name
    /// txtRecords add &lt;name&gt; "&lt;text&gt;" [&lt;ttl&gt;]    the name answers with the text as well
    /// txtRecords remove &lt;name&gt; ["&lt;text&gt;"]        the name no longer answers with the text - with any, where none is given
    /// </code>
    /// A text with spaces in it is put in double quotes, and is one record;
    /// within them \" is a double quote and \\ a backslash, as in a zone file:
    /// txtRecords add example.org "say \"hello\"". A text longer than 255
    /// octets is answered as several character-strings, which a reader puts
    /// together again (RFC 7208 §3.3).
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class TXTRecordsCommand(DNSServerCLI CLI) : TypedRecordsCommand(CLI,
                                                                           DNSResourceRecordTypes.TXT,
                                                                           CommandName,
                                                                           "\"<text>\"",
                                                                           "example.org \"v=spf1 -all\"")
    {

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "txtRecords";

    }

}
