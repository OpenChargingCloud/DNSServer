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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

#endregion

namespace cloud.charging.open.DNSServer.SecureShell
{

    /// <summary>
    /// The DNS clients of the "dnsServer" section, as logins of the node's SSH
    /// server: a machine signs in under its name with one of its keys, for
    /// DNS over SSH and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the queries.</b> The node confines every such login to the
    /// subsystems named here - no terminal, no command line, no SFTP - and
    /// names none while DNS over SSH is switched off, which is also when no
    /// DNS client is let in at all.
    /// </para>
    /// <para>
    /// <b>Its own keys, as OpenSSH holds them.</b> Each key is an
    /// authorized_keys line, so that from="192.168.1.0/24" and
    /// expiry-time="20271231" in front of it hold.
    /// </para>
    /// <para>
    /// <b>Or the certificates of an SSH CA.</b> A line with cert-authority is
    /// the key of a CA, as OpenSSH reads such a line: a user certificate it signed lets the client in where it is
    /// valid and names the client - or, with principals="..." on the line, one
    /// of the names there. What the certificate confines a session to holds as
    /// well, source-address above all, and a certificate that runs out ends the
    /// session at the next check.
    /// </para>
    /// <para>
    /// <b>Asked again while a session lasts.</b> A client taken out of the
    /// section, switched off or left without the key it came with has its
    /// session ended - at the next of the node's checks, every few seconds.
    /// </para>
    /// </remarks>
    public sealed class DNSClientSSHSignIns : ISSHSignIns
    {

        #region Data

        /// <summary>
        /// What a DNS client is to the SSH access rules: "dnsclient:cs001".
        /// </summary>
        public const            String    Kind           = "dnsclient";

        /// <summary>
        /// How seldom one refusal of one client is said again.
        /// </summary>
        public static readonly  TimeSpan  SayAgainAfter  = TimeSpan.FromMinutes(1);

        private readonly Func<Boolean>                                 served;
        private readonly TimeProvider                                  timeProvider;
        private readonly ConcurrentDictionary<String, DateTimeOffset>  said     = [];
        private volatile IReadOnlyDictionary<String, (DNSClientEntry Entry, IReadOnlyList<AuthorizedKey> Keys)>  clients
                                                                        = new Dictionary<String, (DNSClientEntry, IReadOnlyList<AuthorizedKey>)>();

        #endregion

        #region Properties

        /// <summary>
        /// "dnsclient".
        /// </summary>
        public String  PrincipalKind
            => Kind;

        /// <summary>
        /// The subsystem the clients get.
        /// </summary>
        public String  Subsystem      { get; }

        /// <summary>
        /// The clients, as they were last set.
        /// </summary>
        public IReadOnlyList<DNSClientEntry> Clients
            => [.. clients.Values.Select(client => client.Entry).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)];

        #endregion

        #region Events

        /// <summary>
        /// A client's key was let in no further, for a reason somebody can put
        /// right. An unknown key is not said here: the SSH server says every
        /// failed sign-in itself.
        /// </summary>
        public event Action<LogLevel, String>? OnNotice;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The given DNS clients, as SSH logins for the given subsystem.
        /// </summary>
        /// <param name="Clients">The clients.</param>
        /// <param name="Subsystem">The subsystem of DNS over SSH.</param>
        /// <param name="Served">Whether DNS over SSH is served now.</param>
        /// <param name="TimeProvider">The clock keys are held to.</param>
        public DNSClientSSHSignIns(IEnumerable<DNSClientEntry>  Clients,
                                   String                       Subsystem,
                                   Func<Boolean>                Served,
                                   TimeProvider?                TimeProvider = null)
        {

            this.Subsystem     = Subsystem;
            this.served        = Served;
            this.timeProvider  = TimeProvider ?? System.TimeProvider.System;

            Set(Clients);

        }

        #endregion


        #region Set(Clients)

        /// <summary>
        /// The clients from now on. Whoever is no longer among them, or no
        /// longer with the key they came with, is ended at the node's next check.
        /// </summary>
        public void Set(IEnumerable<DNSClientEntry> Clients)
        {

            var parsed = new Dictionary<String, (DNSClientEntry, IReadOnlyList<AuthorizedKey>)>(StringComparer.Ordinal);

            foreach (var client in Clients)
            {

                var keys = new List<AuthorizedKey>();

                foreach (var line in client.Keys)
                    if (AuthorizedKeysFile.TryParseLine(line, out var key) && key is not null)
                        keys.Add(key);

                parsed[client.Name] = (client, keys);

            }

            clients = parsed;

        }

        #endregion


        #region Knows(Username)

        /// <summary>
        /// Whether the name is a DNS client - let in or not.
        /// </summary>
        public Boolean Knows(User_Id Username)

            => clients.ContainsKey(Username.ToString());

        #endregion

        #region AuthorizePublicKeyAsync(Request, CancellationToken)

        public ValueTask<Boolean> AuthorizePublicKeyAsync(SshPublicKeyAuthRequest  Request,
                                                          CancellationToken        CancellationToken = default)

            => ValueTask.FromResult(WhyNotLetIn(Request.Username.ToString(), Request.PublicKeyBlob, Saying: true) is null);

        #endregion

        #region GetRestrictionsAsync(Request, CancellationToken)

        /// <summary>
        /// What the key's own line confines the session to - from= above all.
        /// The node confines it to the queries besides, whatever this says.
        /// </summary>
        public ValueTask<SshSessionRestrictions> GetRestrictionsAsync(SshPublicKeyAuthRequest  Request,
                                                                      CancellationToken        CancellationToken = default)

            => ValueTask.FromResult(
                   FindKey(Request.Username.ToString(), Request.PublicKeyBlob)?.Restrictions
                       ?? SshSessionRestrictions.None
               );

        #endregion

        #region SubsystemsFor(Username) / GroupsOf(Username)

        /// <summary>
        /// DNS over SSH while it is served, and nothing otherwise.
        /// </summary>
        public IReadOnlySet<String> SubsystemsFor(User_Id Username)

            => served()
                   ? new HashSet<String> { Subsystem }
                   : new HashSet<String>();

        /// <summary>
        /// "dnsclients", which is what "group:dnsclients" in the SSH access
        /// rules means.
        /// </summary>
        public IReadOnlyList<String> GroupsOf(User_Id Username)

            => [ "dnsclients" ];

        #endregion

        #region WhyNoLongerIn(Session)

        public String? WhyNoLongerIn(SshSessionInfo Session)
        {

            if (Session.PublicKeyBlob is not Byte[] key)
                return $"the DNS client '{Session.Username}' did not sign in with an SSH key";

            return WhyNotLetIn(Session.Username.ToString(), key, Saying: false);

        }

        #endregion


        #region (private) FindKey(Name, PublicKeyBlob)

        /// <summary>
        /// The line that lets the given key in under the given name: the client's
        /// own key, or for a certificate the CA that signed it for the client.
        /// </summary>
        private AuthorizedKey? FindKey(String  Name,
                                       Byte[]  PublicKeyBlob)
        {

            if (!clients.TryGetValue(Name, out var client))
                return null;

            var now = timeProvider.GetUtcNow();

            if (SshCertificate.TryParse(PublicKeyBlob, out var certificate) && certificate is not null)
                return client.Keys.FirstOrDefault(key => key.IsCertAuthority                    &&
                                                         key.IsValidAt(now)                     &&
                                                         key.Matches(certificate.SignatureKey) &&
                                                         IsCertifiedFor(certificate, key, Name, now));

            return client.Keys.FirstOrDefault(key => !key.IsCertAuthority && key.Matches(PublicKeyBlob) && key.IsValidAt(now));

        }

        #endregion

        #region (private static) IsCertifiedFor(Certificate, Authority, Name, Now)

        /// <summary>
        /// Whether the certificate is a valid user certificate of the CA for the
        /// client: for its name, or with principals="..." on the CA's line for
        /// one of the names there - as OpenSSH reads such a line.
        /// </summary>
        private static Boolean IsCertifiedFor(SshCertificate  Certificate,
                                              AuthorizedKey   Authority,
                                              String          Name,
                                              DateTimeOffset  Now)
        {

            var trust = new SshCertificateAuthorityTrust().TrustCA(Certificate.SignatureKey);

            return (Authority.Principals.Count > 0 ? Authority.Principals : [ Name ]).
                       Any(principal => SshCertificateValidator.Validate(Certificate,
                                                                         SshCertType.User,
                                                                         principal,
                                                                         trust,
                                                                         Now,
                                                                         SshSessionRestrictions.EnforcedCriticalOptions).IsValid);

        }

        #endregion

        #region (private) WhyNotLetIn(Name, PublicKeyBlob, Saying)

        private String? WhyNotLetIn(String   Name,
                                    Byte[]   PublicKeyBlob,
                                    Boolean  Saying)
        {

            if (!served())
                return "DNS over SSH is switched off";

            if (!clients.TryGetValue(Name, out var client))
                return $"there is no DNS client '{Name}' any more";

            // An unknown key is a stranger's, and not worth more than the SSH
            // server's own line.
            if (FindKey(Name, PublicKeyBlob) is null)
                return SshCertificate.TryParse(PublicKeyBlob, out var certificate) && certificate is not null
                           ? $"the certificate '{certificate.KeyId}' (serial {certificate.Serial}) is not signed for the DNS client '{Name}' by a CA it trusts - taken out, run out, or for another name"
                           : $"the key {SshFingerprint.Sha256(PublicKeyBlob)} is not one the DNS client '{Name}' signs in with - taken out, or expired";

            if (!client.Entry.Enabled)
            {

                var why = $"the DNS client '{Name}' is switched off";

                if (Saying)
                    Say(Name, why);

                return why;

            }

            return null;

        }

        #endregion

        #region (private) Say(Name, Why)

        private void Say(String Name, String Why)
        {

            var now = timeProvider.GetUtcNow();
            var key = $"{Name}\n{Why}";

            if (said.TryGetValue(key, out var last) && now - last < SayAgainAfter)
                return;

            said[key] = now;

            OnNotice?.Invoke(LogLevel.Warning, $"A DNS client signing in over SSH was turned away: {Why}.");

        }

        #endregion

    }

}
