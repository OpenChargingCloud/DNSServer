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
    /// The CNAME records of the name server: the names that are aliases of another.
    /// </summary>
    /// <remarks>
    /// <code>
    /// cnameRecords                               every CNAME record
    /// cnameRecords &lt;name&gt;                        the CNAME of one name
    /// cnameRecords add &lt;name&gt; &lt;target&gt; [&lt;ttl&gt;] the name is an alias of the target
    /// cnameRecords remove &lt;name&gt; [&lt;target&gt;]     the name is no longer an alias
    /// </code>
    /// A name with a CNAME has no other record, and one CNAME only (RFC 1034
    /// §3.6.2): a CNAME is refused where the name has records, and a record
    /// where the name has a CNAME. The target is complete, with or without its
    /// trailing dot, and need not be one of this name server's.
    /// </remarks>
    /// <param name="CLI">The command line of the DNS server.</param>
    public class CNAMERecordsCommand(DNSServerCLI CLI) : TypedRecordsCommand(CLI,
                                                                             DNSResourceRecordTypes.CNAME,
                                                                             CommandName,
                                                                             "<target>",
                                                                             "docs.example.org www.example.org")
    {

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "cnameRecords";

    }

}
