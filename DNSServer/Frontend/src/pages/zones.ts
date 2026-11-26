import { api, type ListedZone, type NewZone, type Zone, type ZoneTransfers, type ZonesChanged } from '../api/client';
import { auth } from '../auth';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, field, formatSince, formatTimestamp, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';
import { toURL } from '@node/basePath';

/**
 * The zones the name server is authoritative for: each with the SOA and NS
 * records the server makes for it, and what it lacks to be found - added and
 * removed here.
 *
 * A zone is its name and every name below it. Its SOA and NS records are made
 * by the server, its serial counted on by every change of a record in it. A
 * zone removed takes its SOA and NS records with it; its records stay, and are
 * answered by their name, as every record in no zone is. The hostmaster and
 * the timers of the SOA are changed in the "zones" of the "dnsServer" section,
 * by hand.
 *
 * Its secondaries are set here as well: the addresses and prefixes it may be
 * transferred to (AXFR, IXFR), the TSIG keys a transfer may be signed with
 * instead, and the addresses told of each new serial (NOTIFY) - with the last
 * transfer of each zone, and whether each secondary answered its last NOTIFY.
 */
/** Whom a zone is transferred to, in words: its addresses and prefixes, and its TSIG keys - or nobody. */
export function transferWords(Zone: Zone): string {

    const to = [ ...Zone.transfer, ...Zone.transferKeys.map(key => `TSIG key ${key}`) ];

    return to.length > 0
               ? to.join(', ')
               : 'nobody';

}


export const zonesPage: Page = {

    title: 'Zones',

    render({ root }) {

        const content = shell(root, {
            active:    '/dnsServer/zones',
            title:     'Zones',
            subtitle:  'what the name server is authoritative for.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('nameserver', 'edit');

        let cancelled = false;
        let current: ListedZone[] | null = null;

        /** The zone the form of the secondaries is of. */
        let picked: string | null = null;


        function draw(): void {

            if (current === null)
                return;

            const zones = current;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at the zones', 'change them')}
                    </div>
                `}

                <div class="cards stacked">
                    ${zonesCard(zones)}
                    ${zones.length > 0 ? transferCard(zones) : nothing}
                    ${addCard()}
                </div>

            `);

        }


        /** Every zone, with the records it makes and what removes it. */
        function zonesCard(Zones: ListedZone[]): TemplateResult {

            return html`
                <section class="card" id="zones">

                    <h2><i class="fa-solid fa-sitemap"></i> Zones</h2>

                    ${Zones.length === 0
                          ? html`<p class="hint" id="no-zones">
                                     None yet: the records are answered by their name alone, with no SOA to say which
                                     zone a "not there" is from.
                                 </p>`
                          : repeat(Zones, zone => zone.name, zone => zoneBlock(zone))}

                    <div class="form-actions">
                        <span id="zones-note"  class="form-notice" role="status"></span>
                        <span id="zones-error" class="form-error"  role="alert"></span>
                    </div>

                </section>
            `;

        }


        function zoneBlock(Zone: ListedZone): TemplateResult {

            return html`
                <div class="zone" data-zone="${Zone.name}">

                    <h3>
                        ${Zone.name}
                        <button type="button" class="btn small danger" data-remove-zone="${Zone.name}" ?disabled=${!mayChange}
                                @click=${() => void remove(Zone)}>Remove</button>
                    </h3>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Serial</span>       <span class="v zone-serial">${Zone.serial}</span></div>
                        <div class="kv"><span class="k">Hostmaster</span>   <span class="v zone-hostmaster">${Zone.hostmaster}</span></div>
                        <div class="kv"><span class="k">Name servers</span> <span class="v zone-name-servers">${Zone.nameServers.join(', ')}</span></div>
                        <div class="kv"><span class="k">Negative TTL</span> <span class="v">${Zone.minimum} s</span></div>
                        <div class="kv"><span class="k">Transferred to</span> <span class="v zone-transfer">${transferWords(Zone)}</span></div>
                        <div class="kv"><span class="k">Notifies</span>       <span class="v zone-notify">${Zone.notify.length > 0 ? Zone.notify.join(', ') : 'nobody'}</span></div>
                    </div>

                    ${Zone.note !== null ? html`<p class="form-error zone-note">${Zone.note}</p>` : nothing}

                    ${Zone.lastTransfer !== null ? html`
                        <p class="hint zone-last-transfer">
                            ${Zone.lastTransfer.refused === null
                                  ? `Last ${Zone.lastTransfer.kind} ${formatTimestamp(Zone.lastTransfer.at)} (${formatSince(Zone.lastTransfer.at)}) to ${Zone.lastTransfer.to}: serial ${Zone.lastTransfer.serial}, ${Zone.lastTransfer.records} record${Zone.lastTransfer.records === 1 ? '' : 's'}.`
                                  : `Last ${Zone.lastTransfer.kind} refused ${formatTimestamp(Zone.lastTransfer.at)} (${formatSince(Zone.lastTransfer.at)}) to ${Zone.lastTransfer.to}: ${Zone.lastTransfer.refused}.`}
                        </p>
                    ` : nothing}

                    ${Zone.notified.map(notified => html`
                        <p class="hint zone-notified" data-notified="${notified.to}">
                            NOTIFY of serial ${notified.serial} to ${notified.to} ${formatTimestamp(notified.at)}:
                            ${notified.answered ? 'answered' : `not answered after ${notified.tries} tries`}.
                        </p>
                    `)}

                    <div class="table-scroll">
                        <table class="records dns-records">
                            <thead>
                                <tr><th>Name</th><th>Type</th><th>TTL</th><th>Data</th></tr>
                            </thead>
                            <tbody>
                                ${Zone.records.map(record => html`
                                    <tr data-zone-record="${record.line}">
                                        <td class="record-name">${record.name}</td>
                                        <td class="record-type"><span class="chip">${record.type}</span></td>
                                        <td class="record-ttl">${record.ttl} s</td>
                                        <td class="record-data">${record.data ?? ''}</td>
                                    </tr>
                                `)}
                            </tbody>
                        </table>
                    </div>

                </div>
            `;

        }


        /** The form of the secondaries of a zone: whom it is transferred to, by which keys, and whom it tells. */
        function transferCard(Zones: ListedZone[]): TemplateResult {

            const zone = Zones.find(candidate => candidate.name === picked) ?? Zones[0]!;

            return html`
                <section class="card" id="zone-transfer-card">

                    <h2><i class="fa-solid fa-right-left"></i> Secondaries</h2>

                    <form id="zone-transfer" class="form-stack" @submit=${onTransfer}>

                        <label>Zone
                            <select name="zone" ?disabled=${!mayChange}
                                    @change=${(event: Event) => { picked = (event.target as HTMLSelectElement).value; draw(); }}>
                                ${Zones.map(candidate => html`<option value="${candidate.name}" ?selected=${candidate.name === zone.name}>${candidate.name}</option>`)}
                            </select>
                        </label>

                        <label>Transferred to
                            <input type="text" name="transfer" autocomplete="off" spellcheck="false" ?disabled=${!mayChange}
                                   placeholder="192.0.2.53 2001:db8:53::/48" .value=${zone.transfer.join(' ')} />
                            <span class="hint">
                                The addresses and prefixes of its secondaries, separated by spaces or commas: they may
                                ask for the whole zone (AXFR, IXFR), over TCP. Empty, nobody may - but by a key below.
                            </span>
                        </label>

                        <label>By TSIG keys
                            <input type="text" name="transferKeys" autocomplete="off" spellcheck="false" ?disabled=${!mayChange}
                                   placeholder="ns2-transfer" .value=${zone.transferKeys.join(' ')} />
                            <span class="hint">
                                A transfer signed with one of these keys (RFC 8945) is made from whatever address. The keys
                                are written into the "tsigKeys" of the "dnsServer" section by hand, with their secrets.
                            </span>
                        </label>

                        <label>Notifies
                            <input type="text" name="notify" autocomplete="off" spellcheck="false" ?disabled=${!mayChange}
                                   placeholder="192.0.2.53 [2001:db8:53::1]:5353" .value=${zone.notify.join(' ')} />
                            <span class="hint">
                                The secondaries told of each new serial (NOTIFY), so that they ask now rather than at
                                their next refresh: an address, with a port where not 53.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save the secondaries</button>
                            <span id="zone-transfer-note"  class="form-notice" role="status"></span>
                            <span id="zone-transfer-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function onTransfer(event: SubmitEvent): void {

            event.preventDefault();

            const form   = event.currentTarget as HTMLFormElement;
            const words  = (name: string) => field(form, name).split(/[\s,]+/).filter(word => word.length > 0);

            const transfers: ZoneTransfers = {
                name:          field(form, 'zone'),
                transfer:      words('transfer'),
                transferKeys:  words('transferKeys'),
                notify:        words('notify')
            };

            picked = transfers.name;

            void change(() => api.dnsServer.setTransfer(transfers), '#zone-transfer');

        }


        /** The form of a new zone: its name, and the name servers where not "ns." and the zone. */
        function addCard(): TemplateResult {

            return html`
                <section class="card" id="add-zone-card">

                    <h2><i class="fa-solid fa-plus"></i> A new zone</h2>

                    <form id="add-zone" class="form-stack" @submit=${onAdd}>

                        <label>Name
                            <input type="text" name="name" required autocomplete="off" spellcheck="false"
                                   placeholder="example.org" ?disabled=${!mayChange} />
                            <span class="hint">
                                The zone is this name and every name below it. Complete, with or without its trailing dot.
                            </span>
                        </label>

                        <label>Name servers
                            <input type="text" name="nameServers" autocomplete="off" spellcheck="false"
                                   placeholder="ns1.example.org ns2.example.org" ?disabled=${!mayChange} />
                            <span class="hint">
                                What its NS records name, separated by spaces or commas; the first is the primary of its
                                SOA. Empty, "ns." and the zone. A name server in the zone needs an A or AAAA record -
                                on the <a href="${toURL('/dnsServer/records')}">Records</a> page - for a resolver to find it.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Add the zone</button>
                            <span id="add-zone-note"  class="form-notice" role="status"></span>
                            <span id="add-zone-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function onAdd(event: SubmitEvent): void {

            event.preventDefault();

            const form         = event.currentTarget as HTMLFormElement;
            const name         = field(form, 'name');
            const nameServers  = field(form, 'nameServers').split(/[\s,]+/).filter(nameServer => nameServer.length > 0);

            const zone: NewZone = { name };

            if (nameServers.length > 0)
                zone.nameServers = nameServers;

            void change(() => api.dnsServer.addZone(zone), '#add-zone', form);

        }


        function remove(Zone: ListedZone): Promise<void> {

            if (!confirm(`Remove the zone ${Zone.name}? Its SOA and NS records go with it; its records stay, and are answered by their name alone.`))
                return Promise.resolve();

            return change(() => api.dnsServer.removeZone(Zone.name), '#zones');

        }


        /**
         * Send a change, and draw the zones the node has: what it made of it,
         * not what was sent. Said in the line of the form it came from, or of
         * the list.
         */
        async function change(Send:  () => Promise<ZonesChanged>,
                              From:  '#add-zone' | '#zones' | '#zone-transfer',
                              Form:  HTMLFormElement | null = null): Promise<void> {

            const note   = content.querySelector<HTMLElement>(`${From}-note`);
            const error  = content.querySelector<HTMLElement>(`${From}-error`);

            for (const line of content.querySelectorAll<HTMLElement>('.form-notice, .form-error:not(.zone-note)'))
                line.textContent = '';

            try
            {

                const changed = await whileSaving(content, note, Send);

                current = changed.zones;

                draw();
                Form?.reset();

                const said = content.querySelector<HTMLElement>(`${From}-note`);

                if (said !== null)
                    said.textContent = [ changed.said, changed.note ?? '', 'Saved, and in effect.' ].filter(part => part.length > 0).join(' ');

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

                const loaded = await api.dnsServer.zones();

                if (cancelled)
                    return;

                current = loaded;

                draw();
                content.querySelector<HTMLFormElement>('#add-zone')?.reset();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The zones could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};
