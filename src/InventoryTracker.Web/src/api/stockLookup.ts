/**
 * The current stock of each line on a form (docs/03-architecture.md, part v1.2, section 16 "Live stock per line";
 * decision #232). The send, complementary-send and completion forms and the ship dialog show each line's stock
 * (AC-42.1, US-43, US-47, AC-53.1). They call {@link readStockFor} once when they open; it is never refreshed by
 * itself (F-14).
 *
 * Two ways to read it, chosen by how many distinct items the form has:
 * - up to {@link perItemReadLimit}: one `GET /stock/{itemId}` per item, at most {@link perItemReadsAtOnce} at a time;
 * - more: pages of `GET /stock` (100 rows each), stopping as soon as every item has been found. With the PRD's 500
 *   items that is at most 5 requests instead of 500.
 */
import { readStockItem, readStockPage, type ItemType, type StockRow } from './stock';
import { allPagesPageSize } from './paging';

/**
 * Architecture 16 and risk 12: with this many distinct items or fewer, each is read on its own; with more, the stock
 * list is paged. Real orders have 1 to 10 lines (PRD section 5). A judgement, kept in one place on purpose.
 */
export const perItemReadLimit = 10;

/** Architecture 16: at most this many single-item reads run at the same time, so a form does not flood the API. */
export const perItemReadsAtOnce = 4;

/** Decision #214, design 18.1: shown under a form when any line's stock could not be read. */
export function stockLookupFailedText(action: 'send' | 'ship'): string {
  return `Warehouse stock could not be loaded. You can still ${action}; the API checks the stock.`;
}

/**
 * Reads the current stock of the given items. The answer has an entry for every item that was read; an item whose
 * read failed, or that the API does not list, has none, and its line shows "-" with {@link stockLookupFailedText}
 * (decision #214). The figures are only shown, never compared: the API checks the stock when the form is sent (row 70).
 *
 * `itemType` narrows the paged reading: `RawMaterial` on the production forms, none for a shipment, which can hold
 * both kinds. Never throws.
 */
export async function readStockFor(
  itemIds: readonly number[],
  itemType?: ItemType,
): Promise<ReadonlyMap<number, StockRow>> {
  // A Set keeps each value once, so an item on two lines is read once.
  const wanted = [...new Set(itemIds)];
  if (wanted.length === 0) {
    return new Map();
  }
  return wanted.length <= perItemReadLimit ? readEachItem(wanted) : readStockPages(new Set(wanted), itemType);
}

/**
 * Up to 10 items: one read per item, at most 4 running at once. A failed read only leaves its own item out, except
 * when the session has ended: then no further read is started (T-039 review).
 */
async function readEachItem(itemIds: readonly number[]): Promise<Map<number, StockRow>> {
  const found = new Map<number, StockRow>();
  let next = 0;
  // AC-34.6, architecture 15: once any read is answered `401` `not-authenticated`, the session module is ending the
  // session and the sign-in screen is about to open. Every further read would get the same `401`, so none is started.
  // Reads already running are left to finish; their answers no longer matter.
  let sessionEnded = false;

  // A small "worker pool": each worker takes the next item that nobody has taken yet, waits for its answer, and goes
  // on until none is left. JavaScript runs one piece of code at a time, so `next += 1` can never be taken twice;
  // the waiting for answers is what overlaps.
  async function worker(): Promise<void> {
    while (!sessionEnded && next < itemIds.length) {
      const itemId = itemIds[next];
      next += 1;
      // Never true: the loop condition already keeps `next` inside the list. The check is there only because the
      // strict setting `noUncheckedIndexedAccess` (tsconfig.json) types every `list[i]` as possibly undefined.
      if (itemId === undefined) {
        return;
      }
      const result = await readStockItem(itemId);
      if (result.ok) {
        found.set(itemId, result.data);
      } else if (result.failure.kind === 'session-ended') {
        sessionEnded = true;
      }
    }
  }

  // Promise.all waits until every worker has finished. With 3 items, 3 workers are enough.
  const workers = Math.min(perItemReadsAtOnce, itemIds.length);
  await Promise.all(Array.from({ length: workers }, () => worker()));
  return found;
}

/**
 * More than 10 items: the stock list, 100 rows a page, one page after another, until every wanted item has been found
 * or the list ends. A failed page stops the reading: the items found before it are kept, the others are left out.
 */
async function readStockPages(wanted: ReadonlySet<number>, itemType: ItemType | undefined): Promise<Map<number, StockRow>> {
  const found = new Map<number, StockRow>();
  let read = 0;
  for (let page = 1; ; page += 1) {
    const result = await readStockPage({ itemType, page, pageSize: allPagesPageSize });
    if (!result.ok) {
      return found;
    }
    for (const row of result.data.items) {
      if (wanted.has(row.itemId)) {
        found.set(row.itemId, row);
      }
    }
    read += result.data.items.length;
    // Stop as soon as every item is found, or at the end of the list (the same end rule as readAllPages in paging.ts).
    const listEnded = result.data.items.length < allPagesPageSize || read >= result.data.totalCount;
    if (found.size === wanted.size || listEnded) {
      return found;
    }
  }
}
