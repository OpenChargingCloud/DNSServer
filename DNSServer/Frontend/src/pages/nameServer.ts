import { api, type NameServerSettings, type NameServerStatus } from '../api/client';
import { toURL } from '@node/basePath';
import type { Page } from '@node/router';
import { reloadButton, shell } from '@node/shell';
import { errorMessage } from '@node/ui';
import { html, nothing, render, type TemplateResult } from '@node/view';

/**
 * The name server: whether it answers, where - UDP and TCP - how many records
 * of which type it answers with, and how much it answered since the start.
 * Read-only: the records are changed on the Records page below this one, and
 * where it listens in the "dnsServer" section of the configuration file or by
 * the switches of a start.
 */
export const nameServerPage: Page = {

    title: 'Name server',

    render({ root }) {

        const content = shell(root, {
            active:    '/dnsServer',
            title:     'Name server',
            subtitle:  'where it answers DNS, and how much it answered.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        let cancelled = false;


        function draw(Status: NameServerStatus, Settings: NameServerSettings): void {

            render(content, html`
                <div class="cards stacked">
                    ${listenersCard(Status, Settings)}
                    ${recordsCard(Status, Settings)}
                    ${answeredCard(Status)}
                </div>
            `);

        }


        function listenersCard(Status: NameServerStatus, Settings: NameServerSettings): TemplateResult {

            return html`
                <section class="card" id="listeners">

                    <h2><i class="fa-solid fa-globe"></i> DNS</h2>

                    <p class="dns-state">
                        ${Status.serving
                              ? html`<span class="chip ok" id="serving">answering</span>`
                              : html`<span class="chip warn" id="serving">not answering</span>
                                     <span class="muted">See the <a href="${toURL('/logs')}">log</a>.</span>`}
                    </p>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Address</span> <span class="v" id="listen">${Status.listen === 'any' ? 'every address of both families' : Status.listen}</span></div>
                        <div class="kv"><span class="k">UDP</span>     <span class="v" id="udp">${Status.udp ?? (Settings.udp ? 'not listening' : 'switched off')}</span></div>
                        <div class="kv"><span class="k">TCP</span>     <span class="v" id="tcp">${Status.tcp ?? (Settings.tcp ? 'not listening' : 'switched off')}</span></div>
                    </div>

                    ${askWith(Status)}

                    <p class="hint">
                        Where it listens is said by the "dnsServer" section of the configuration file, or by
                        <code>--dns-port</code>, <code>--listen</code>, <code>--no-udp</code> and <code>--no-tcp</code>
                        at a start.
                    </p>

                </section>
            `;

        }


        function recordsCard(Status: NameServerStatus, Settings: NameServerSettings): TemplateResult {

            const types = Object.entries(Status.recordTypes);

            return html`
                <section class="card" id="records-summary">

                    <h2><i class="fa-solid fa-list"></i> Records</h2>

                    <p id="record-count">
                        ${Status.records === 0
                              ? 'No record yet: every name asked for is answered as not there.'
                              : `${Status.records} record${Status.records === 1 ? '' : 's'}: ${types.map(([ type, count ]) => `${count} ${type}`).join(', ')}.`}
                    </p>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Default TTL</span> <span class="v" id="default-ttl">${ttlWords(Settings.defaultTTL)}</span></div>
                        <div class="kv"><span class="k">Zones</span>       <span class="v" id="zone-names">${Status.zones.length === 0
                                                                                                         ? 'none: the records are answered by their name alone'
                                                                                                         : Status.zones.join(', ')}</span></div>
                    </div>

                    <p class="hint">
                        The records are listed, added and removed on the <a href="${toURL('/dnsServer/records')}">Records</a> page,
                        the zones - with the SOA and NS records the server makes for them - on the
                        <a href="${toURL('/dnsServer/zones')}">Zones</a> page.
                    </p>

                </section>
            `;

        }


        function answeredCard(Status: NameServerStatus): TemplateResult {

            return html`
                <section class="card" id="answered">

                    <h2><i class="fa-solid fa-chart-simple"></i> Answered</h2>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Requests</span>  <span class="v" id="requests">${Status.requestsReceived}</span></div>
                        <div class="kv"><span class="k">Responses</span> <span class="v" id="responses">${Status.responsesSent}</span></div>
                    </div>

                    <p class="hint">Since the program started, over UDP and TCP.</p>

                </section>
            `;

        }


        async function load(): Promise<void> {

            try
            {

                const [status, settings] = await Promise.all([
                    api.dnsServer.status(),
                    api.dnsServer.settings()
                ]);

                if (!cancelled)
                    draw(status, settings);

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The name server could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        void load();

        return () => { cancelled = true; };

    }

};


/**
 * The line dig asks this name server with, where it listens on one address -
 * and on the loopback address where it listens on every one.
 */
export function askWith(Status: NameServerStatus): TemplateResult | typeof nothing {

    if (Status.udp === null && Status.tcp === null)
        return nothing;

    const address  = Status.listen === 'any' ? '127.0.0.1' : Status.listen;
    const port     = Number((Status.udp ?? Status.tcp ?? '').split(':').pop());
    const tcp      = Status.udp === null ? ' +tcp' : '';

    return html`<pre class="ask-with" id="ask-with">dig @${address}${port === 53 || Number.isNaN(port) ? '' : ` -p ${port}`}${tcp} www.example.org A</pre>`;

}


/** A TTL as somebody reads it: "3600 s (1 h)". */
export function ttlWords(Seconds: number): string {

    const units: [ number, string ][] = [ [ 86400, 'd' ], [ 3600, 'h' ], [ 60, 'min' ] ];

    for (const [ size, unit ] of units)
        if (Seconds >= size && Seconds % size === 0)
            return `${Seconds} s (${Seconds / size} ${unit})`;

    return `${Seconds} s`;

}
