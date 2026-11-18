import { api, type RecordsChanged, type ResourceRecord } from '../api/client';
import { auth } from '../auth';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, field, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * The records the name server answers with: every one of them, of every type,
 * and the A records added and removed here.
 *
 * Every change is sent at once and is in effect at once - answered from then
 * on, and written to the configuration file - and the page draws the records
 * as the node answers with them, not as they were sent. A record of another
 * type - AAAA, MX, TXT - is written into the "dnsServer" section by hand, and
 * is listed here from the next start on.
 */
export const recordsPage: Page = {

    title: 'Records',

    render({ root }) {

        const content = shell(root, {
            active:    '/dnsServer/records',
            title:     'Records',
            subtitle:  'what the name server answers with.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('nameserver', 'edit');

        let cancelled = false;
        let current: ResourceRecord[] | null = null;


        function draw(): void {

            if (current === null)
                return;

            const records = current;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at the records', 'change them')}
                    </div>
                `}

                <div class="cards stacked">
                    ${recordsCard(records)}
                    ${addACard()}
                </div>

            `);

        }


        /** Every record, each A record with what removes it. */
        function recordsCard(Records: ResourceRecord[]): TemplateResult {

            return html`
                <section class="card" id="records">

                    <h2><i class="fa-solid fa-list"></i> Records</h2>

                    ${Records.length === 0
                          ? html`<p class="hint" id="no-records">None yet: every name asked for is answered as not there.</p>`
                          : html`
                              <div class="table-scroll">
                                  <table class="records dns-records">
                                      <thead>
                                          <tr><th>Name</th><th>Type</th><th>TTL</th><th>Data</th><th></th></tr>
                                      </thead>
                                      <tbody>
                                          ${repeat(Records, record => record.line, record => recordRow(record))}
                                      </tbody>
                                  </table>
                              </div>
                          `}

                    <div class="form-actions">
                        <span id="records-note"  class="form-notice" role="status"></span>
                        <span id="records-error" class="form-error"  role="alert"></span>
                    </div>

                </section>
            `;

        }


        function recordRow(Record: ResourceRecord): TemplateResult {

            return html`
                <tr data-record="${Record.line}">
                    <td class="record-name">${Record.name}</td>
                    <td class="record-type"><span class="chip">${Record.type}</span></td>
                    <td class="record-ttl">${Record.ttl} s</td>
                    <td class="record-data">${Record.data ?? ''}</td>
                    <td class="record-actions">
                        ${Record.type === 'A' ? html`
                            <button type="button" class="btn small danger" data-remove="${Record.name} ${Record.data ?? ''}" ?disabled=${!mayChange}
                                    @click=${() => void removeA(Record)}>Remove</button>
                        ` : nothing}
                    </td>
                </tr>
            `;

        }


        /** The form of a new A record: a name, its IPv4 address, and a TTL where not the default. */
        function addACard(): TemplateResult {

            return html`
                <section class="card" id="add-a-card">

                    <h2><i class="fa-solid fa-plus"></i> A new A record</h2>

                    <form id="add-a" class="form-stack" @submit=${onAddA}>

                        <label>Name
                            <input type="text" name="name" required autocomplete="off" spellcheck="false"
                                   placeholder="www.example.org" ?disabled=${!mayChange} />
                            <span class="hint">Complete, with or without its trailing dot.</span>
                        </label>

                        <label>IPv4 address
                            <input type="text" name="address" required autocomplete="off" spellcheck="false" inputmode="decimal"
                                   placeholder="192.0.2.10" ?disabled=${!mayChange} />
                            <span class="hint">A name may answer with several: each is a record of its own.</span>
                        </label>

                        <label>TTL
                            <input type="text" name="ttl" autocomplete="off" spellcheck="false"
                                   placeholder="1h" ?disabled=${!mayChange} />
                            <span class="hint">
                                How long a resolver may keep the answer: seconds, or with s, m, h or d after them. Empty, the
                                default TTL of the "dnsServer" section. The A records of one name share their TTL, so a TTL
                                given here is the others' as well.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Add the record</button>
                            <span id="add-a-note"  class="form-notice" role="status"></span>
                            <span id="add-a-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function onAddA(event: SubmitEvent): void {

            event.preventDefault();

            const form     = event.currentTarget as HTMLFormElement;
            const name     = field(form, 'name');
            const address  = field(form, 'address');
            const ttl      = field(form, 'ttl');

            void change(() => api.dnsServer.addA(ttl.length > 0 ? { name, address, ttl } : { name, address }),
                        '#add-a', form);

        }


        function removeA(Record: ResourceRecord): Promise<void> {

            if (!confirm(`Remove ${Record.name} ${Record.data ?? ''}? It is no longer answered with from then on.`))
                return Promise.resolve();

            return change(() => api.dnsServer.removeA({ name: Record.name, address: Record.data ?? '' }), '#records');

        }


        /**
         * Send a change, and draw the records the node answers with: what it
         * made of it, not what was sent. Said in the line of the form it came
         * from, or of the list.
         */
        async function change(Send:  () => Promise<RecordsChanged>,
                              From:  '#add-a' | '#records',
                              Form:  HTMLFormElement | null = null): Promise<void> {

            const note   = content.querySelector<HTMLElement>(`${From}-note`);
            const error  = content.querySelector<HTMLElement>(`${From}-error`);

            for (const line of content.querySelectorAll<HTMLElement>('.form-notice, .form-error'))
                line.textContent = '';

            try
            {

                const changed = await whileSaving(content, note, Send);

                current = changed.records;

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

                const loaded = await api.dnsServer.records();

                if (cancelled)
                    return;

                current = loaded;

                // Loaded anew - Reload - is what the node has, the form too.
                draw();
                content.querySelector<HTMLFormElement>('#add-a')?.reset();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The records could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};
