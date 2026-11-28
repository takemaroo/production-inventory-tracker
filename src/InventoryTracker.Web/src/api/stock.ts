/**
 * The stock endpoints, as the front end reads them (docs/02-design.md section 5.5; US-10, US-31).
 *
 * `GET /stock/{itemId}` gives one item's current figures; `GET /stock` gives a page of them, optionally for one item
 * type. Every figure is calculated by the API; the front end shows them and computes nothing (PRD row 70).
 */
import { apiRequest, type ApiResult } from './http';
import type { ApiNumber } from './json';
import { readPage, type Page } from './paging';

/** The two kinds of item (design 5.4): bought materials and the products the factory makes. */
export type ItemType = 'RawMaterial' | 'FinishedProduct';

/**
 * One item's stock (design 5.5). Quantities are {@link ApiNumber}: exact text unless they are safe whole numbers
 * (architecture 16), and always in `unit`.
 */
export interface StockRow {
  readonly itemId: number;
  readonly itemName: string;
  readonly itemType: ItemType;
  /** The unit's name, for example "kg" or "Bag"; every quantity in the row is in it. */
  readonly unit: string;
  readonly warehouse: ApiNumber;
  readonly productionStorage: ApiNumber;
  /** D-12, AC-31.1: the quantity on Created sales orders; 0, never null. Information only. */
  readonly openDemand: ApiNumber;
  /** D-3: committed to the InProduction order; null for a finished product. */
  readonly committedToOrder: ApiNumber | null;
  readonly committedOrderNumber: number | null;
  /** AC-10.3: in Pending returns; null for a finished product. */
  readonly pendingReturns: ApiNumber | null;
  /** D-4: free leftover in production storage; null for a finished product. */
  readonly freeLeftover: ApiNumber | null;
}

/** The stock list's path, without the "/api/v1" base. */
export const stockPath = '/stock';

/** `GET /stock/{itemId}`: one item's stock. A `404` is a refusal with the API's "Item {id} not found." */
export function readStockItem(itemId: number): Promise<ApiResult<StockRow>> {
  // encodeURIComponent keeps the id one path segment whatever it holds; an id is a number, so it is unchanged.
  return apiRequest<StockRow>('GET', `${stockPath}/${encodeURIComponent(String(itemId))}`);
}

/** `GET /stock`: one page of stock rows, optionally for one item type (F-6: 50 rows unless `pageSize` says otherwise). */
export function readStockPage(
  query: { readonly itemType?: ItemType; readonly page?: number; readonly pageSize?: number } = {},
): Promise<ApiResult<Page<StockRow>>> {
  return readPage<StockRow>(stockPath, query);
}
