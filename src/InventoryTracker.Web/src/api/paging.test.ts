import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { pageFromAddress, readAllPages, readPage } from './paging';

// A stubbed fetch, as in http.test.ts: no network, every call recorded.
const fetchStub = vi.fn<typeof fetch>();

beforeEach(() => {
  fetchStub.mockReset();
  vi.stubGlobal('fetch', fetchStub);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/**
 * Makes the stub answer like a list endpoint over `total` records with ids 1 to `total`, reading `page` and
 * `pageSize` from each request's address. `failOnPage` answers that page with a 500 instead.
 */
function serveList(total: number, failOnPage?: number): void {
  fetchStub.mockImplementation(async (input) => {
    const url = new URL(String(input), 'http://localhost');
    const page = Number(url.searchParams.get('page'));
    const pageSize = Number(url.searchParams.get('pageSize'));
    if (page === failOnPage) {
      return new Response(null, { status: 500 });
    }
    const first = (page - 1) * pageSize + 1;
    const items = Array.from({ length: Math.max(0, Math.min(pageSize, total - first + 1)) }, (_, i) => ({ id: first + i }));
    return new Response(JSON.stringify({ items, page, pageSize, totalCount: total }), { status: 200 });
  });
}

/** The addresses the stub was asked for, in order. */
function requestedAddresses(): string[] {
  return fetchStub.mock.calls.map(([input]) => String(input));
}

describe('readPage (F-6, design 2.5)', () => {
  // F-6: list screens ask for 50 rows and page 1 unless told otherwise; the filters go under the API's names.
  it('asks for page 1 of 50 by default, with the filters', async () => {
    serveList(132);

    const result = await readPage<{ id: number }>('/production-orders', { status: 'Created' });

    expect(requestedAddresses()).toEqual(['/api/v1/production-orders?status=Created&page=1&pageSize=50']);
    expect(result.ok && result.data).toMatchObject({ page: 1, pageSize: 50, totalCount: 132 });
    expect(result.ok && result.data.items).toHaveLength(50);
  });

  // F-6: a list screen asks for the page in its address.
  it('asks for the page and size it is given', async () => {
    serveList(132);

    await readPage('/production-orders', { page: 3, pageSize: 50 });

    expect(requestedAddresses()).toEqual(['/api/v1/production-orders?page=3&pageSize=50']);
  });
});

describe('pageFromAddress (F-6, architecture 16)', () => {
  // Architecture 16: the page in the address is a whole number of 1 or more, otherwise 1.
  it.each([
    ['2', 2],
    ['1', 1],
    ['2147483647', 2147483647],
  ])('reads %j as page %i', (value, page) => {
    expect(pageFromAddress(value)).toBe(page);
  });

  // Architecture 16, design 12.1: any other value counts as not set, so page 1 opens.
  it.each([null, '', '0', '-1', '2.5', '1e2', ' 2', 'abc', '2147483648', '99999999999999999999'])('reads %j as page 1', (value) => {
    expect(pageFromAddress(value)).toBe(1);
  });
});

describe('readAllPages (F-13, PRD row 81)', () => {
  // F-13, row 81: every record, in pages of 100, one after another; it stops at totalCount (250 records: 3 requests).
  it('reads pages of 100 until totalCount records are read', async () => {
    serveList(250);

    const result = await readAllPages<{ id: number }>('/items', { type: 'RawMaterial' });

    expect(requestedAddresses()).toEqual([
      '/api/v1/items?type=RawMaterial&page=1&pageSize=100',
      '/api/v1/items?type=RawMaterial&page=2&pageSize=100',
      '/api/v1/items?type=RawMaterial&page=3&pageSize=100',
    ]);
    expect(result.ok && result.data.map((item) => item.id)).toEqual(Array.from({ length: 250 }, (_, i) => i + 1));
  });

  // Stops at totalCount: with exactly 200 records, no third (empty) page is asked for. Row 81: 500 records, 5 requests.
  it.each([
    [200, 2],
    [500, 5],
    [0, 1],
    [1, 1],
  ])('reads %i records in %i requests', async (total, requests) => {
    serveList(total);

    const result = await readAllPages('/customers');

    expect(fetchStub).toHaveBeenCalledTimes(requests);
    expect(result.ok && result.data).toHaveLength(total);
  });

  // A list that shrinks while it is read (totalCount says more, but the page is empty) cannot make it loop.
  it('stops at an empty page even if totalCount says there is more', async () => {
    fetchStub.mockImplementation(async () => new Response('{"items":[],"page":2,"pageSize":100,"totalCount":300}'));

    const result = await readAllPages('/units');

    expect(fetchStub).toHaveBeenCalledTimes(1);
    expect(result).toEqual({ ok: true, status: 200, data: [] });
  });

  // F-13 with F-2: a failed page stops the reading and is returned; part of a list is never shown as all of it.
  it('returns the first failure and reads no further page', async () => {
    serveList(500, 2);

    const result = await readAllPages('/items');

    expect(fetchStub).toHaveBeenCalledTimes(2);
    expect(result).toEqual({ ok: false, failure: { kind: 'no-message', status: 500 } });
  });
});
