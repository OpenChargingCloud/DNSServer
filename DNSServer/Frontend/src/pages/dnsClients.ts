import { api,
         type DNSClient,
         type DNSClientEntry,
         type DNSClientKey,
         type DNSOverSSH,
         type SSHAccount,
         type SSHActivity,
         type SSHChange }  from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, field, formatSince, formatTimestamp, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * The DNS clients: the machines that may ask the name server over SSH - the
 * subsystem dns@open.charging.cloud of the node's SSH server - each under its
 * name, with one of its SSH keys, and confined to the queries: no terminal, no
 * command line, no SFTP.
 *
 * Every change is sent at once and is in effect at once, the whole list with
 * it: a client switched off or removed can ask nothing from then on, and a
 * session of it that is open ends. The two forms - a new client, and another
 * key of one - are drafts the page holds until they are sent.
 *
 * Beside each client, what it asked last - when, from where, what - and how
 * often since the program started; so for the accounts of the node that asked
 * under their own names.
 */
export const dnsClientsPage: Page = {

    title: 'DNS clients',

    render({ root }) {

        const content = shell(root, {
            active:    '/dnsServer/clients',
            title:     'DNS clients',
            subtitle:  'the machines that may ask the name server over SSH, by their SSH keys.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('nameserver', 'edit');

        let cancelled = false;
        let current: DNSOverSSH | null = null;


        function draw(): void {

            if (current === null)
                return;

            const ssh = current;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at the DNS clients', 'change them')}
                    </div>
                `}

                <div class="cards stacked">
                    ${serviceCard(ssh)}
                    ${clientsCard(ssh)}
                    ${ssh.accounts.length > 0 ? accountsCard(ssh.accounts) : nothing}
                    ${addClientCard(ssh)}
                    ${ssh.clients.length > 0 ? addKeyCard(ssh) : nothing}
                </div>

            `);

        }


        /** Whether DNS is answered over SSH, where, and how a client asks there. */
        function serviceCard(SSH: DNSOverSSH): TemplateResult {

            const onlyHere = SSH.url !== null && /\/\/(127\.0\.0\.1|\[::1\]|localhost)[:/]/.test(SSH.url);

            return html`
                <section class="card" id="dns-ssh">

                    <h2><i class="fa-solid fa-terminal"></i> DNS over SSH</h2>

                    <p class="dns-state">
                        ${SSH.served
                              ? html`<span class="chip ok" id="ssh-served">answered</span> <code id="ssh-url">${SSH.url ?? ''}</code>`
                              : SSH.enabled
                                    ? html`<span class="chip warn" id="ssh-served">switched on, not answered</span>
                                           <span class="muted">The SSH server of this node is off - see the
                                           <a href="${toURL('/configuration/ssh')}">SSH server</a> page.</span>`
                                    : html`<span class="chip" id="ssh-served">off</span>`}
                    </p>

                    ${SSH.served && onlyHere ? html`
                        <div class="notice" id="ssh-only-here">
                            The SSH server listens on this machine only, so no other machine can reach it: start with
                            <code>--any</code> for DNS over SSH in the local network.
                        </div>
                    ` : nothing}

                    <div class="kv-list">
                        <div class="kv"><span class="k">Subsystem</span> <span class="v"><code>${SSH.subsystem}</code></span></div>
                        <div class="kv"><span class="k">Host key</span>  <span class="v"><code class="fingerprint" id="host-key">${SSH.hostKey ?? 'not known yet'}</code></span></div>
                    </div>

                    <p class="hint">
                        A client holds this server to its host key, signs in under its name with one of its keys and asks
                        for the subsystem, which carries DNS as over TCP: a two-octet length before every message. It is
                        answered with the records and zones UDP and TCP are answered with - a charging station that runs
                        OCPP over SSH resolves its names over the connection it has anyway.
                    </p>

                    <div class="form-actions">
                        <button type="button" class="btn" id="switch-ssh" ?disabled=${!mayChange}
                                @click=${() => void change({ enabled: !SSH.enabled }, SSH.enabled ? 'Switched off.' : 'Switched on.')}>
                            ${SSH.enabled ? 'Switch DNS over SSH off' : 'Switch DNS over SSH on'}
                        </button>
                    </div>

                </section>
            `;

        }


        /** The clients and their keys, each with what may be done to it. */
        function clientsCard(SSH: DNSOverSSH): TemplateResult {

            return html`
                <section class="card" id="clients">

                    <h2><i class="fa-solid fa-server"></i> Clients</h2>

                    ${SSH.clients.length === 0
                          ? html`<p class="hint" id="no-clients">None yet: no machine may ask over SSH but an account that may run "dnsquery".</p>`
                          : html`
                              <div class="table-scroll">
                                  <table class="records dns-clients">
                                      <thead>
                                          <tr><th>Name</th><th>Keys</th><th>Last asked</th><th></th></tr>
                                      </thead>
                                      <tbody>
                                          ${repeat(SSH.clients, client => client.name, client => clientRow(client))}
                                      </tbody>
                                  </table>
                              </div>
                          `}

                    <div class="form-actions">
                        <span id="clients-note"  class="form-notice" role="status"></span>
                        <span id="clients-error" class="form-error"  role="alert"></span>
                    </div>

                </section>
            `;

        }


        function clientRow(Client: DNSClient): TemplateResult {

            return html`
                <tr data-client="${Client.name}" class="${Client.enabled ? '' : 'switched-off'}">
                    <td class="client-name">
                        <strong>${Client.name}</strong>
                        ${Client.enabled ? nothing : html`<span class="chip">off</span>`}
                    </td>
                    <td class="client-keys">
                        ${Client.keys.length === 0
                              ? html`<span class="muted">no key: it cannot sign in</span>`
                              : repeat(Client.keys, key => key.line, key => html`
                                  <div class="client-key" data-fingerprint="${key.fingerprint ?? ''}">
                                      <code class="fingerprint">${key.fingerprint ?? key.line}</code>
                                      ${key.certAuthority === true ? html`<span class="chip on" title="The key of an SSH CA: the user certificates it signs let the client in">CA</span>` : nothing}
                                      <span class="muted small">${keyDetails(key, Client.name)}</span>
                                      <button type="button" class="btn small" data-remove-key="${key.line}" ?disabled=${!mayChange}
                                              @click=${() => void removeKey(Client, key.line)}>Remove key</button>
                                  </div>
                              `)}
                    </td>
                    <td class="client-activity">${activityLines(Client.activity)}</td>
                    <td class="client-actions">
                        <button type="button" class="btn small" data-switch="${Client.name}" ?disabled=${!mayChange}
                                @click=${() => void switchClient(Client)}>${Client.enabled ? 'Switch off' : 'Switch on'}</button>
                        <button type="button" class="btn small danger" data-remove="${Client.name}" ?disabled=${!mayChange}
                                @click=${() => void removeClient(Client)}>Remove</button>
                    </td>
                </tr>
            `;

        }


        /**
         * The accounts of the node that asked over SSH under their own names:
         * what they may do is their roles', not this page's.
         */
        function accountsCard(Accounts: SSHAccount[]): TemplateResult {

            return html`
                <section class="card" id="accounts">

                    <h2><i class="fa-solid fa-user"></i> Accounts</h2>

                    <p class="hint">
                        Accounts of this node that may run "dnsquery" asked over SSH under their own names. They are no
                        DNS clients: what they may do is their roles'.
                    </p>

                    <div class="table-scroll">
                        <table class="records dns-accounts">
                            <thead>
                                <tr><th>Account</th><th>Last asked</th></tr>
                            </thead>
                            <tbody>
                                ${repeat(Accounts, account => account.name, account => html`
                                    <tr data-account="${account.name}">
                                        <td class="client-name"><strong>${account.name}</strong></td>
                                        <td class="client-activity">${activityLines(account.activity)}</td>
                                    </tr>
                                `)}
                            </tbody>
                        </table>
                    </div>

                </section>
            `;

        }


        /** The form of a new client: its name, and its first key. */
        function addClientCard(SSH: DNSOverSSH): TemplateResult {

            return html`
                <section class="card" id="add-client-card">

                    <h2><i class="fa-solid fa-plus"></i> A new client</h2>

                    <form id="add-client" class="form-stack" @submit=${onAddClient}>

                        <label>Name
                            <input type="text" name="name" required autocomplete="off" spellcheck="false"
                                   placeholder="cs001" ?disabled=${!mayChange} />
                            <span class="hint">What it signs in as. Not the name of an account of this node.</span>
                        </label>

                        <label>Its key
                            <textarea name="key" rows="3" required spellcheck="false" ?disabled=${!mayChange}
                                      placeholder="ssh-ed25519 AAAA... cs001"></textarea>
                            <span class="hint">
                                One line of an <code>authorized_keys</code> file: the public key, as
                                <code>ssh-keygen</code> wrote it. <code>from="192.168.1.0/24"</code> or
                                <code>expiry-time="20271231"</code> in front of it hold as OpenSSH holds them.
                                With <code>cert-authority</code> in front, it is the key of an SSH CA: the user
                                certificates it signs let the client in where they are made out to its name - or, with
                                <code>principals="station-2"</code>, to one of the names there.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Add the client</button>
                            <span id="add-client-note"  class="form-notice" role="status"></span>
                            <span id="add-client-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                    ${SSH.clients.length > 0 ? nothing : html`<p class="hint">It may ask as soon as it is added.</p>`}

                </section>
            `;

        }


        /** The form of another key of a client: a machine with a new key, before the old one goes. */
        function addKeyCard(SSH: DNSOverSSH): TemplateResult {

            return html`
                <section class="card" id="add-key-card">

                    <h2><i class="fa-solid fa-key"></i> Another key of a client</h2>

                    <form id="add-key" class="form-stack" @submit=${onAddKey}>

                        <label>Client
                            <select name="client" ?disabled=${!mayChange}>
                                ${SSH.clients.map(client => html`<option value="${client.name}">${client.name}</option>`)}
                            </select>
                        </label>

                        <label>The key
                            <textarea name="key" rows="3" required spellcheck="false" ?disabled=${!mayChange}
                                      placeholder="ssh-ed25519 AAAA..."></textarea>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Add the key</button>
                            <span id="add-key-note"  class="form-notice" role="status"></span>
                            <span id="add-key-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function onAddClient(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;
            const name = field(form, 'name');
            const key  = oneLine(field(form, 'key'));

            if (current!.clients.some(client => client.name.toLowerCase() === name.toLowerCase()))
            {
                must<HTMLElement>(content, '#add-client-error').textContent = `There is a client "${name}" already: give it another key below.`;
                return;
            }

            void change({ clients: [ ...entriesOf(current!), { name, keys: [ key ], enabled: true } ] },
                        `${name} may ask over SSH now.`, '#add-client', form);

        }


        function onAddKey(event: SubmitEvent): void {

            event.preventDefault();

            const form  = event.currentTarget as HTMLFormElement;
            const name  = field(form, 'client');
            const key   = oneLine(field(form, 'key'));

            void change({ clients: entriesOf(current!).map(entry => entry.name === name
                                                                        ? { ...entry, keys: [ ...entry.keys, key ] }
                                                                        : entry) },
                        `${name} has another key.`, '#add-key', form);

        }


        function switchClient(Client: DNSClient): Promise<void> {

            return change({ clients: entriesOf(current!).map(entry => entry.name === Client.name
                                                                          ? { ...entry, enabled: !Client.enabled }
                                                                          : entry) },
                          Client.enabled ? `${Client.name} is switched off.` : `${Client.name} is switched on.`);

        }


        function removeClient(Client: DNSClient): Promise<void> {

            if (!confirm(`Remove ${Client.name}? It can ask nothing over SSH from then on, and a session of it that is open ends.`))
                return Promise.resolve();

            return change({ clients: entriesOf(current!).filter(entry => entry.name !== Client.name) },
                          `${Client.name} is removed.`);

        }


        function removeKey(Client: DNSClient, Line: string): Promise<void> {

            if (Client.keys.length === 1 &&
                !confirm(`This is the only key of ${Client.name}: without it, it cannot sign in. Remove it?`))
                return Promise.resolve();

            return change({ clients: entriesOf(current!).map(entry => entry.name === Client.name
                                                                          ? { ...entry, keys: entry.keys.filter(key => key !== Line) }
                                                                          : entry) },
                          `A key of ${Client.name} is removed.`);

        }


        /**
         * Send a change, and draw what the node answers with: the page shows
         * what the node made of it, not what was sent. Said in the line of the
         * form it came from, or of the list.
         */
        async function change(Change:  SSHChange,
                              Done:    string,
                              From:    '#add-client' | '#add-key' | null = null,
                              Form:    HTMLFormElement | null = null): Promise<void> {

            const notePrefix = From ?? '#clients';
            const note       = content.querySelector<HTMLElement>(`${notePrefix}-note`);
            const error      = content.querySelector<HTMLElement>(`${notePrefix}-error`);

            for (const line of content.querySelectorAll<HTMLElement>('.form-notice, .form-error'))
                line.textContent = '';

            try
            {
                current = await whileSaving(content, note, () => api.dnsServer.setSSH(Change));

                draw();
                Form?.reset();

                const said = content.querySelector<HTMLElement>(`${notePrefix}-note`);

                if (said !== null)
                    said.textContent = `${Done} Saved, and in effect.`;
            }
            catch (problem)
            {
                if (error !== null)
                    error.textContent = errorMessage(problem);
            }

        }


        async function load(): Promise<void> {

            try
            {

                const loaded = await api.dnsServer.ssh();

                if (cancelled)
                    return;

                current = loaded;

                // Loaded anew - Reload - is what the node has, the forms too.
                draw();
                content.querySelector<HTMLFormElement>('#add-client')?.reset();
                content.querySelector<HTMLFormElement>('#add-key')?.reset();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The DNS clients could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};


/** The clients as they are sent back: each key as the line it was given as. */
export function entriesOf(SSH: DNSOverSSH): DNSClientEntry[] {

    return SSH.clients.map(client => ({
               name:     client.name,
               keys:     client.keys.map(key => key.line),
               enabled:  client.enabled
           }));

}

/**
 * A key as one line: pasted from a terminal, a long key often arrives broken
 * over several, and an authorized_keys line is one.
 */
export function oneLine(Text: string): string {
    return Text.split(/\r?\n/).map(part => part.trim()).filter(part => part.length > 0).join('');
}


/**
 * What a key is, beside its fingerprint: its algorithm, for the key of a CA
 * the names its certificates have to be made out to, its comment, its other
 * options and its end.
 */
export function keyDetails(Key: DNSClientKey, ClientName: string): string {

    const isCA    = Key.certAuthority === true;

    // A CA's own options are said in words, not as they were written.
    const options = (Key.options ?? []).filter(option => !isCA || !/^(cert-authority$|principals=)/i.test(option));

    return [
               Key.algorithm ?? '',
               isCA ? `certificates made out to ${(Key.principals ?? [ ClientName ]).join(', ')}` : '',
               Key.comment ?? '',
               options.join(', '),
               Key.notAfter ? `until ${formatTimestamp(Key.notAfter)}` : ''
           ].filter(part => part.length > 0).join(' - ');

}


/**
 * What a login asked last - when, from where, what - and how often since the
 * program started.
 */
export function activityLines(Activity: SSHActivity): TemplateResult {

    if (Activity.lastAt === null)
        return html`<span class="muted">nothing asked since the start</span>`;

    return html`
        <div class="last-asked">
            ${formatTimestamp(Activity.lastAt)} <span class="muted">(${formatSince(Activity.lastAt)})</span>
            <div class="muted small">
                ${[ Activity.lastQuestion !== null ? Activity.lastQuestion : '',
                    Activity.lastFrom     !== null ? `from ${Activity.lastFrom}` : '' ].filter(part => part.length > 0).join(', ')}
            </div>
        </div>
        <div class="muted small activity-counts">
            ${Activity.queries} quer${Activity.queries === 1 ? 'y' : 'ies'} since the start
        </div>
    `;

}
