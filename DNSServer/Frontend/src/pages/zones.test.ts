/**
 * The zones page drawn, in a document of happy-dom, against a stand-in DNS
 * server that keeps what it is sent: a zone added with the name servers typed
 * - or without, for "ns." and the zone - listed with the SOA and NS records it
 * makes and what it lacks to be found, removed after asking, what the server
 * refused said beside the form, and for whoever may only look, nothing that
 * changes anything.
 */

import { asked, field, open, refused, said, submit, type, until, type Asked } from '../../test/dnsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { zonesPage } = await import('./zones.ts');


interface Kept { name: string; nameServers: string[] }

let zones: Kept[] = [];

function full(Name: string): string {
    return Name.endsWith('.') ? Name : `${Name}.`;
}

function asZone(Zone: Kept): Record<string, unknown> {

    const soa = `${Zone.nameServers[0]} hostmaster.${Zone.name} 2026112500 86400 7200 1209600 3600`;

    return {
        name:         Zone.name,
        nameServers:  Zone.nameServers,
        hostmaster:   `hostmaster@${Zone.name.slice(0, -1)}`,
        serial:       2026112500,
        refresh:      86400,
        retry:        7200,
        expire:       1209600,
        minimum:      3600,
        records:      [ { name: Zone.name, type: 'SOA', class: 'IN', ttl: 3600, data: soa, value: null, line: `${Zone.name} 3600 IN SOA ${soa}` },
                        ...Zone.nameServers.map(nameServer => ({ name: Zone.name, type: 'NS', class: 'IN', ttl: 3600, data: nameServer, value: null, line: `${Zone.name} 3600 IN NS ${nameServer}` })) ],
        note:         Zone.nameServers[0]!.endsWith(Zone.name) ? `${Zone.nameServers[0]} has no address yet.` : null
    };

}

function dnsServer({ method, path, body }: Asked): unknown {

    if (path !== '/dnsServer/zones')
        return undefined;

    if (method === 'GET')
        return zones.map(asZone);

    const sent = body as { name: string; nameServers?: string[] };
    const name = full(sent.name);

    if (method === 'ADD') {

        if (zones.some(zone => zone.name === name))
            return refused(400, `The name server is authoritative for ${name} already.`);

        zones = [ ...zones, { name, nameServers: (sent.nameServers ?? [ `ns.${name}` ]).map(full) } ];

        return { said: `The name server is authoritative for ${name} from now on.`, note: null, zones: zones.map(asZone) };

    }

    if (method === 'REMOVE') {

        zones = zones.filter(zone => zone.name !== name);

        return { said: `The name server is no longer authoritative for ${name}.`, note: null, zones: zones.map(asZone) };

    }

    return undefined;

}

/** What was sent to change the zones: ADD and REMOVE, each with its path and body. */
function sent(): { method: string; path: string; body: unknown }[] {
    return asked.filter(one => one.method !== 'GET').map(one => ({ method: one.method, path: one.path, body: one.body }));
}

const mayEdit = [ 'nameserver:read', 'nameserver:edit' ];


describe('the zones', () => {

    it('adds a zone without name servers - for "ns." and the zone - and lists what it makes', async () => {

        zones = [];

        const root = await open(zonesPage, '/dnsServer/zones', mayEdit,
                                dnsServer, root => root.querySelector('#no-zones') !== null);

        type(field(root, '#add-zone', 'name'), 'example.org');
        submit(root, '#add-zone');

        await until(() => root.querySelector('[data-zone="example.org."]') !== null, 'the zone was not drawn');

        assert.deepEqual(sent(), [ { method: 'ADD', path: '/dnsServer/zones', body: { name: 'example.org' } } ]);
        assert.match(root.querySelector('#add-zone-note')!.textContent!, /authoritative for example\.org\. from now on\. Saved, and in effect\./);
        assert.equal(field(root, '#add-zone', 'name').value, '', 'the form kept what was sent');

        const zone = root.querySelector('[data-zone="example.org."]')!;

        assert.equal(zone.querySelector('.zone-serial')!.textContent,        '2026112500');
        assert.equal(zone.querySelector('.zone-name-servers')!.textContent,  'ns.example.org.');
        assert.equal(zone.querySelectorAll('tr[data-zone-record]').length,   2, 'its SOA and one NS record');
        assert.match(zone.querySelector('.zone-note')!.textContent!,         /ns\.example\.org\. has no address yet/);

    });

    it('sends the name servers as they were typed, separated by spaces or commas', async () => {

        zones = [];

        const root = await open(zonesPage, '/dnsServer/zones', mayEdit,
                                dnsServer, root => root.querySelector('#add-zone') !== null);

        type(field(root, '#add-zone', 'name'),        'example.org.');
        type(field(root, '#add-zone', 'nameServers'), ' ns1.example.net, ns2.example.net  ');
        submit(root, '#add-zone');

        await until(() => root.querySelector('[data-zone="example.org."]') !== null, 'the zone was not drawn');

        assert.deepEqual(sent()[0]!.body, { name: 'example.org.', nameServers: [ 'ns1.example.net', 'ns2.example.net' ] });
        assert.equal(root.querySelectorAll('[data-zone="example.org."] tr[data-zone-record]').length, 3, 'its SOA and two NS records');
        assert.equal(root.querySelector('.zone-note'), null, 'name servers outside the zone are no one\'s to check');

    });

    it('says what the server refused, and keeps what was typed', async () => {

        zones = [ { name: 'example.org.', nameServers: [ 'ns.example.org.' ] } ];

        const root = await open(zonesPage, '/dnsServer/zones', mayEdit,
                                dnsServer, root => root.querySelector('[data-zone]') !== null);

        type(field(root, '#add-zone', 'name'), 'example.org');
        submit(root, '#add-zone');

        await until(() => root.querySelector('#add-zone-error')!.textContent !== '', 'the refusal was not said');

        assert.match(root.querySelector('#add-zone-error')!.textContent!, /already/);
        assert.equal(field(root, '#add-zone', 'name').value, 'example.org');

    });

    it('removes a zone after asking, and leaves the others', async () => {

        zones = [ { name: 'example.org.', nameServers: [ 'ns.example.org.' ] },
                  { name: 'example.net.', nameServers: [ 'ns.example.org.' ] } ];

        const root = await open(zonesPage, '/dnsServer/zones', mayEdit,
                                dnsServer, root => root.querySelectorAll('[data-zone]').length === 2);

        root.querySelector<HTMLButtonElement>('button[data-remove-zone="example.org."]')!.click();

        await until(() => root.querySelectorAll('[data-zone]').length === 1, 'the zone was not removed');

        assert.ok(said.some(text => /Remove the zone example\.org\.\?/.test(text)), 'nobody was asked before it went');
        assert.deepEqual(sent(), [ { method: 'REMOVE', path: '/dnsServer/zones', body: { name: 'example.org.' } } ]);
        assert.match(root.querySelector('#zones-note')!.textContent!, /no longer authoritative for example\.org\./);
        assert.notEqual(root.querySelector('[data-zone="example.net."]'), null);

    });

    it('changes nothing for whoever may only look, and says why', async () => {

        zones = [ { name: 'example.org.', nameServers: [ 'ns.example.org.' ] } ];

        const root = await open(zonesPage, '/dnsServer/zones', [ 'nameserver:read' ],
                                dnsServer, root => root.querySelector('[data-zone]') !== null);

        assert.match(root.querySelector('.notice')!.textContent!, /may look at the zones but not change them/);

        for (const control of root.querySelectorAll<HTMLButtonElement>('button[data-remove-zone], #add-zone button[type="submit"]'))
            assert.equal(control.disabled, true, `${control.outerHTML} can be used`);

    });

});
