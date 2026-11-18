/**
 * The configuration drawn, in a document of happy-dom, against a stand-in
 * DNS server: its cards stand as the markup they are - the name server's
 * status not among them, which the Name server page draws - the frame's
 * Reload asks again and draws what it says then, and a configuration that
 * cannot be had is said so.
 */

import { asked, open, refused, until, type Asked } from '../../test/dnsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { configurationPage } = await import('./configuration.ts');


let uptime     = '1 minute';
let available  = true;

function dnsServer({ path }: Asked): unknown {

    if (path === '/status')
        return { service: 'DNS server', version: '0.1.0', hermod: null, timestamp: '2026-11-09T12:00:00Z',
                 startedAt: '2026-11-09T11:59:00Z', uptime, sessions: 1, log: { entries: 0, capacity: 100, lastId: 0, tags: [] } };

    if (path === '/configuration')
        return available
                   ? { dnsServer: { machine: 'dns001', settings: { listen: 'everywhere-test' }, status: { serving: true, secretStatusField: 'not here' } },
                       http: { port: 2362 }, web: {}, log: {}, time: {},
                       assemblies: [ { name: 'DNSServer', version: '0.1.0', commit: 'abc1234' } ] }
                   : refused(500, 'the configuration file could not be read');

    return undefined;

}


describe('the configuration', () => {

    it('draws its cards as markup, without the status the Name server page draws, and again on Reload', async () => {

        uptime     = '1 minute';
        available  = true;

        const root = await open(configurationPage, '/configuration', [ 'configuration:read' ],
                                dnsServer, root => root.querySelector('.cards') !== null);

        assert.equal(root.querySelectorAll('.cards > section.card').length, 6);
        assert.match(root.querySelector('.cards')!.textContent!, /dns001/);
        assert.match(root.querySelector('.cards')!.textContent!, /everywhere-test/);
        assert.doesNotMatch(root.querySelector('.cards')!.textContent!, /secretStatusField|not here/);
        assert.match(root.querySelector('.cards')!.textContent!, /Uptime\s+1 minute/);

        const configurationsAsked = () => asked.filter(one => one.method === 'GET' && one.path === '/configuration').length;
        assert.equal(configurationsAsked(), 1);

        uptime = '2 minutes';
        root.querySelector<HTMLButtonElement>('.page-actions #reload')!.click();

        await until(() => /Uptime\s+2 minutes/.test(root.textContent!), 'Reload did not draw what the server says then');

        assert.equal(configurationsAsked(), 2, 'Reload did not ask again');

    });

    it('says so where the configuration cannot be had', async () => {

        available = false;

        const root = await open(configurationPage, '/configuration', [ 'configuration:read' ],
                                dnsServer, root => root.querySelector('.error-box') !== null);

        assert.match(root.querySelector('.error-box')!.textContent!, /The configuration could not be loaded: .*could not be read/);

    });

});
