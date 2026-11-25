import { api, changeableTypes, isChangeable, keyOf, numbersOf, type ChangeableType, type NewRecord, type RecordNumber, type RecordsChanged, type ResourceRecord } from '../api/client';
import { auth } from '../auth';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, field, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * The records the name server answers with: every one of them, of every type,
 * and the A, AAAA, TXT, CNAME, MX, SRV and PTR records added and removed here.
 *
 * Every change is sent at once and is in effect at once - answered from then
 * on, and written to the configuration file - and the page draws the records
 * as the node answers with them, not as they were sent. A record of another
 * type - CAA, NAPTR - is written into the "dnsServer" section by hand, and is
 * listed here from the next start on.
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

        /** The type the form makes a record of. */
        let picked: ChangeableType = 'A';


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
                    ${addCard()}
                </div>

            `);

        }


        /** Every record, each of a type that is changed here with what removes it. */
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

            const type = Record.type;

            return html`
                <tr data-record="${Record.line}">
                    <td class="record-name">${Record.name}</td>
                    <td class="record-type"><span class="chip">${type}</span></td>
                    <td class="record-ttl">${Record.ttl} s</td>
                    <td class="record-data">${Record.data ?? ''}</td>
                    <td class="record-actions">
                        ${isChangeable(type) && Record.value !== null ? html`
                            <button type="button" class="btn small danger" data-remove="${Record.name} ${type} ${Record.value}" ?disabled=${!mayChange}
                                    @click=${() => void remove(type, Record)}>Remove</button>
                        ` : nothing}
                    </td>
                </tr>
            `;

        }


        /** What each number of an MX or SRV record is called, an example, and what to know. */
        const numbers: Record<RecordNumber, { label: string; placeholder: string; hint: string }> = {
            preference:  { label: 'Preference', placeholder: '10',  hint: 'The lowest is tried first.' },
            priority:    { label: 'Priority',   placeholder: '0',   hint: 'The lowest is tried first.' },
            weight:      { label: 'Weight',     placeholder: '5',   hint: 'Among those of one priority, how often each is chosen.' },
            port:        { label: 'Port',       placeholder: '443', hint: 'Where the service listens on the target.' }
        };

        /** What the form asks for, for each type: what the data is called, an example, and what to know. */
        const asked: Record<ChangeableType, { label: string; placeholder: string; inputmode: string; hint: string; name?: string }> = {
            A:      { label:        'IPv4 address',
                      placeholder:  '192.0.2.10',
                      inputmode:    'decimal',
                      hint:         'A name may answer with several: each is a record of its own.' },
            AAAA:   { label:        'IPv6 address',
                      placeholder:  '2001:db8::10',
                      inputmode:    'text',
                      hint:         'A name may answer with several: each is a record of its own.' },
            TXT:    { label:        'Text',
                      placeholder:  'v=spf1 -all',
                      inputmode:    'text',
                      hint:         'Taken as it is, spaces and quotes included - no quotes around it. A text longer than ' +
                                    '255 octets is answered in pieces, which a reader puts together again.' },
            CNAME:  { label:        'Alias of',
                      placeholder:  'www.example.org',
                      inputmode:    'text',
                      hint:         'The name this one is an alias of, complete. A name with a CNAME has no other record, ' +
                                    'and one CNAME only.' },
            MX:     { label:        'Mail exchange',
                      placeholder:  'mail.example.org',
                      inputmode:    'text',
                      hint:         'The host that takes the mail of the name: a name with an A or AAAA record, not an address.',
                      name:         'example.org' },
            SRV:    { label:        'Target',
                      placeholder:  'csms.example.org',
                      inputmode:    'text',
                      hint:         'The host the service is on: a name with an A or AAAA record.',
                      name:         '_ocpp._tcp.example.org' },
            PTR:    { label:        'Points to',
                      placeholder:  'www.example.org',
                      inputmode:    'text',
                      hint:         'The name the address - or the name - points to, complete.',
                      name:         '192.0.2.10' }
        };


        /** The form of a new record: its type, a name, what it answers with, and a TTL where not the default. */
        function addCard(): TemplateResult {

            const ask = asked[picked];

            return html`
                <section class="card" id="add-record-card">

                    <h2><i class="fa-solid fa-plus"></i> A new record</h2>

                    <form id="add-record" class="form-stack" @submit=${onAdd}>

                        <label>Type
                            <select name="type" id="add-record-type" ?disabled=${!mayChange}
                                    @change=${(event: Event) => pick((event.target as HTMLSelectElement).value)}>
                                ${changeableTypes.map(type => html`<option value="${type}" ?selected=${type === picked}>${type}</option>`)}
                            </select>
                        </label>

                        <label>Name
                            <input type="text" name="name" required autocomplete="off" spellcheck="false"
                                   placeholder="${ask.name ?? 'www.example.org'}" ?disabled=${!mayChange} />
                            <span class="hint" id="add-record-name-hint">${picked === 'PTR'
                                ? 'Complete, with or without its trailing dot - or an IPv4 or IPv6 address, for the name it is looked up by: 192.0.2.10 is 10.2.0.192.in-addr.arpa.'
                                : picked === 'SRV'
                                      ? 'The name of the service, complete: _service._protocol and the domain.'
                                      : 'Complete, with or without its trailing dot.'}</span>
                        </label>

                        ${numbersOf(picked).map(number => html`
                            <label>${numbers[number].label}
                                <input type="number" name="${number}" required min="${number === 'port' ? 1 : 0}" max="65535" step="1"
                                       placeholder="${numbers[number].placeholder}" ?disabled=${!mayChange} />
                                <span class="hint">${numbers[number].hint}</span>
                            </label>
                        `)}

                        <label><span id="add-record-data-label">${ask.label}</span>
                            <input type="text" name="data" required autocomplete="off" spellcheck="false"
                                   inputmode="${ask.inputmode}" placeholder="${ask.placeholder}" ?disabled=${!mayChange} />
                            <span class="hint" id="add-record-data-hint">${ask.hint}</span>
                        </label>

                        <label>TTL
                            <input type="text" name="ttl" autocomplete="off" spellcheck="false"
                                   placeholder="1h" ?disabled=${!mayChange} />
                            <span class="hint">
                                How long a resolver may keep the answer: seconds, or with s, m, h or d after them. Empty, the
                                default TTL of the "dnsServer" section. The records of one name and type share their TTL, so a
                                TTL given here is the others' as well.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Add the record</button>
                            <span id="add-record-note"  class="form-notice" role="status"></span>
                            <span id="add-record-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        /** Another type picked: the form asks for what that type answers with - what was typed stays. */
        function pick(Type: string): void {

            if (!isChangeable(Type) || Type === picked)
                return;

            picked = Type;
            draw();

        }


        function onAdd(event: SubmitEvent): void {

            event.preventDefault();

            const form    = event.currentTarget as HTMLFormElement;
            const type    = picked;
            const name    = field(form, 'name');
            // A text is answered with as it is; every other value is trimmed.
            const data    = field(form, 'data', type !== 'TXT');
            const ttl     = field(form, 'ttl');

            const record: NewRecord = { name, [keyOf(type)]: data };

            for (const number of numbersOf(type))
                record[number] = Number(field(form, number));

            if (ttl.length > 0)
                record.ttl = ttl;

            void change(() => api.dnsServer.add(type, record), '#add-record', form);

        }


        function remove(Type: ChangeableType, Record: ResourceRecord): Promise<void> {

            if (!confirm(`Remove ${Record.name} ${Type} ${Record.data ?? ''}? It is no longer answered with from then on.`))
                return Promise.resolve();

            return change(() => api.dnsServer.remove(Type, { name: Record.name, [keyOf(Type)]: Record.value ?? '' }), '#records');

        }


        /**
         * Send a change, and draw the records the node answers with: what it
         * made of it, not what was sent. Said in the line of the form it came
         * from, or of the list.
         */
        async function change(Send:  () => Promise<RecordsChanged>,
                              From:  '#add-record' | '#records',
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
                content.querySelector<HTMLFormElement>('#add-record')?.reset();

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
