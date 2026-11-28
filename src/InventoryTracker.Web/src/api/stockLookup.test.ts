import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { StockRow } from './stock';
import { perItemReadLimit, perItemReadsAtOnce, readStockFor, stockLookupFailedText } from './stockLookup';

// A stubbed fetch that answers like the stock endpoints (design 5.5) over a list of records with ids 1 to
// `recordCount`, so the tests can count and inspect every request. No network is used.
const fetchStub = vi.fn<typeof fetch>();
let recordCount = 500;
let failingItems = new Set<number>();
let failingPage: number | undefined;
let sessionEndedItems = new Set<number>();
let inFlight = 0;
let mostInFlight = 0;

beforeEach(() => {
  recordCount = 500;
  failingItems = new Set();
  failingPage = undefined;
  sessionEndedItems = new Set();
  inFlight = 0;
  mostInFlight = 0;
  fetchStub.mockReset();
  fetchStub.mockImplementation(serveStock);
  vi.stubGlobal('fetch', fetchStub);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function stockRow(itemId: number): StockRow {
  return {
    itemId,
    itemName: `Item ${itemId}`,
    itemType: 'RawMaterial',
    unit: 'kg',
    warehouse: '1015.5',
    productionStorage: 0,
    openDemand: 0,
    committedToOrder: 0,
    committedOrderNumber: null,
    pendingReturns: 0,
    freeLeftover: 0,
  };
}

async function serveStock(input: RequestInfo | URL): Promise<Response> {
  // Count how many requests are waiting at once; the answer comes after a short real pause, as from a server.
  inFlight += 1;
  mostInFlight = Math.max(mostInFlight, inFlight);
  await new Promise((resolve) => setTimeout(resolve, 1));
  inFlight -= 1;

  const url = new URL(String(input), 'http://localhost');
  const single = /^\/api\/v1\/stock\/(\d+)$/.exec(url.pathname);
  if (single !== null) {
    const itemId = Number(single[1]);
    if (failingItems.has(itemId)) {
      return new Response(null, { status: 500 });
    }
    if (sessionEndedItems.has(itemId)) {
      // AC-34.6: the API's answer when the token has expired (design 7.1).
      const detail = 'Your sign-in is missing or has expired. Sign in again.';
      return new Response(JSON.stringify({ status: 401, code: 'not-authenticated', detail }), { status: 401 });
    }
    if (itemId > recordCount) {
      return new Response(JSON.stringify({ status: 404, code: 'not-found', detail: `Item ${itemId} not found.` }), { status: 404 });
    }
    return new Response(JSON.stringify(stockRow(itemId)), { status: 200 });
  }

  const page = Number(url.searchParams.get('page'));
  const pageSize = Number(url.searchParams.get('pageSize'));
  if (page === failingPage) {
    throw new TypeError('Failed to fetch');
  }
  const first = (page - 1) * pageSize + 1;
  const items = Array.from({ length: Math.max(0, Math.min(pageSize, recordCount - first + 1)) }, (_, i) => stockRow(first + i));
  return new Response(JSON.stringify({ items, page, pageSize, totalCount: recordCount }), { status: 200 });
}

function requestedAddresses(): string[] {
  return fetchStub.mock.calls.map(([input]) => String(input));
}

/** The ids 1 to n. */
function ids(n: number, from = 1): number[] {
  return Array.from({ length: n }, (_, i) => from + i);
}

describe('readStockFor: 10 items or fewer, one read per item (decision #232)', () => {
  // Architecture 16: 3 items give 3 item reads, and every figure comes back as the API sent it.
  it('reads 3 items with 3 item reads', async () => {
    const stock = await readStockFor([3, 4, 7], 'RawMaterial');

    expect(requestedAddresses().sort()).toEqual(['/api/v1/stock/3', '/api/v1/stock/4', '/api/v1/stock/7']);
    expect([...stock.keys()].sort((a, b) => a - b)).toEqual([3, 4, 7]);
    expect(stock.get(3)?.warehouse).toBe('1015.5');
  });

  // Architecture 16: at most 4 at a time. With 10 items, 4 run together, never more.
  it('runs at most 4 reads at a time', async () => {
    const stock = await readStockFor(ids(perItemReadLimit));

    expect(fetchStub).toHaveBeenCalledTimes(10);
    expect(mostInFlight).toBe(perItemReadsAtOnce);
    expect(perItemReadsAtOnce).toBe(4);
    expect(stock.size).toBe(10);
  });

  // Architecture 16, decision #232: one read per *distinct* item, so an item on two lines is read once.
  it('reads each distinct item once', async () => {
    await readStockFor([3, 3, 4, 3]);

    expect(requestedAddresses().sort()).toEqual(['/api/v1/stock/3', '/api/v1/stock/4']);
  });

  // Decision #214: a failed read leaves only that item missing; the line shows "-" and the form can still be sent.
  it('leaves an item whose read failed missing, and keeps the others', async () => {
    failingItems = new Set([4]);
    recordCount = 6;

    const stock = await readStockFor([3, 4, 7, 5]);

    expect(fetchStub).toHaveBeenCalledTimes(4);
    expect([...stock.keys()].sort((a, b) => a - b)).toEqual([3, 5]);
    expect(stock.has(4)).toBe(false);
    expect(stock.has(7)).toBe(false);
  });

  // AC-34.6, architecture 15 (T-039 review): once a read is answered 401 not-authenticated the session is ending, so
  // no further read is started. The first 4 start together; item 1's 401 stops the other 6 from being sent.
  it('starts no further read once the session has ended', async () => {
    sessionEndedItems = new Set([1]);

    await readStockFor(ids(perItemReadLimit));

    expect(fetchStub).toHaveBeenCalledTimes(perItemReadsAtOnce);
    expect(requestedAddresses().sort()).toEqual(['/api/v1/stock/1', '/api/v1/stock/2', '/api/v1/stock/3', '/api/v1/stock/4']);
  });

  // Architecture 16, decision #232: the stock is read once when the form opens, for the form's own items (F-14);
  // a form with no lines has no items, so nothing is requested.
  it('sends nothing for a form with no lines', async () => {
    expect((await readStockFor([])).size).toBe(0);
    expect(fetchStub).not.toHaveBeenCalled();
  });
});

describe('readStockFor: more than 10 items, the stock list in pages of 100 (decision #232)', () => {
  // Architecture risk 12: the threshold is one constant, 10.
  it('pages the stock list from the 11th distinct item, with the item type of the production forms', async () => {
    expect(perItemReadLimit).toBe(10);

    const stock = await readStockFor(ids(11), 'RawMaterial');

    expect(requestedAddresses()).toEqual(['/api/v1/stock?itemType=RawMaterial&page=1&pageSize=100']);
    expect(stock.size).toBe(11);
  });

  // Architecture 16: 500 items over 500 stock records give at most 5 page reads (PRD section 5, row 81).
  it('reads 500 items in 5 page reads', async () => {
    const stock = await readStockFor(ids(500), 'RawMaterial');

    expect(fetchStub).toHaveBeenCalledTimes(5);
    expect(stock.size).toBe(500);
  });

  // Stopping as soon as every item is found: items all within the first 200 records need 2 pages, not 5.
  it('stops as soon as every item has been found', async () => {
    const stock = await readStockFor([...ids(15), 150, 200]);

    expect(requestedAddresses()).toEqual(['/api/v1/stock?page=1&pageSize=100', '/api/v1/stock?page=2&pageSize=100']);
    expect(stock.size).toBe(17);
  });

  // A shipment can hold both item types, so it reads the list with no filter. An item the list does not hold
  // (deleted meanwhile) is missing once the list ends, after at most 5 reads.
  it('stops at the end of the list and leaves an unlisted item missing', async () => {
    const stock = await readStockFor([...ids(20), 9999]);

    expect(fetchStub).toHaveBeenCalledTimes(5);
    expect(requestedAddresses().every((address) => !address.includes('itemType'))).toBe(true);
    expect(stock.size).toBe(20);
    expect(stock.has(9999)).toBe(false);
  });

  // Decision #214: a failed page stops the reading; items found before it are kept, the rest are missing.
  it('keeps what was found before a failed page and reads no further', async () => {
    failingPage = 3;

    const stock = await readStockFor([5, 150, 250, 350, ...ids(10, 400)]);

    expect(fetchStub).toHaveBeenCalledTimes(3);
    expect([...stock.keys()].sort((a, b) => a - b)).toEqual([5, 150]);
  });
});

describe('stockLookupFailedText (decision #214, design 18.1)', () => {
  // Design 18.1: the exact sentence, with "ship" in the ship dialog.
  it('uses the design text word for word', () => {
    expect(stockLookupFailedText('send')).toBe('Warehouse stock could not be loaded. You can still send; the API checks the stock.');
    expect(stockLookupFailedText('ship')).toBe('Warehouse stock could not be loaded. You can still ship; the API checks the stock.');
  });
});
