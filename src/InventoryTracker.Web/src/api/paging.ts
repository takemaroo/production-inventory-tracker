/**
 * Reading the API's paged lists (docs/03-architecture.md, part v1.2, section 16 "Paging"; decision #240).
 *
 * Every list endpoint answers one page at a time in the envelope of design 2.5. List screens show one page of 50 rows
 * (F-6). Pickers and the stock lookup need every record, so {@link readAllPages} reads pages of 100, the API's largest,
 * one after another (F-13, PRD row 81: 500 records are 5 requests).
 */
import { apiRequest, type ApiResult, type Query } from './http';

/** Design 2.5: one page of a list, exactly as the API sends it. `T` is the type of one row. */
export interface Page<T> {
  readonly items: readonly T[];
  /** The page number, from 1. */
  readonly page: number;
  readonly pageSize: number;
  /** How many records match the filters, across all pages. */
  readonly totalCount: number;
}

/** F-6: list screens show 50 rows per page. */
export const listPageSize = 50;

/** Design 2.5: the most rows the API gives in one page; used when every record is needed (F-13). */
export const allPagesPageSize = 100;

// The API's page number is a C# int, so 2,147,483,647 is the largest it can read; anything larger is refused with 400.
const largestPage = 2_147_483_647;

/**
 * F-6, architecture 16: the page to show, from the `page` value in the screen's address. A whole number from 1 counts;
 * anything else (missing, "0", "-1", "2.5", "abc", too large) means page 1, the same rule as any unknown filter value
 * (design 12.1).
 */
export function pageFromAddress(value: string | null): number {
  if (value === null || !/^\d+$/.test(value)) {
    return 1;
  }
  const page = Number(value);
  return page >= 1 && page <= largestPage ? page : 1;
}

/**
 * Reads one page of a list. `query` holds the list's filters under the API's names, plus `page` (default 1) and
 * `pageSize` (default 50, F-6).
 */
export function readPage<T>(
  path: string,
  query: Query & { readonly page?: number; readonly pageSize?: number } = {},
): Promise<ApiResult<Page<T>>> {
  return apiRequest<Page<T>>('GET', path, {
    query: { ...query, page: query.page ?? 1, pageSize: query.pageSize ?? listPageSize },
  });
}

/**
 * F-13: reads every record of a list, pages of 100 one after another, and stops when `totalCount` records have been
 * read. It also stops at an empty or short page, so a list that shrinks while it is being read cannot make it loop
 * (each page is read once, F-14: the data is as it was when the reading started). The first failure stops the reading
 * and is returned; a part of a list is never returned as if it were all of it.
 */
export async function readAllPages<T>(path: string, query: Query = {}): Promise<ApiResult<T[]>> {
  const items: T[] = [];
  for (let page = 1; ; page += 1) {
    const result = await readPage<T>(path, { ...query, page, pageSize: allPagesPageSize });
    if (!result.ok) {
      return result;
    }
    items.push(...result.data.items);
    const lastPage = result.data.items.length < allPagesPageSize || items.length >= result.data.totalCount;
    if (lastPage) {
      return { ok: true, status: result.status, data: items };
    }
  }
}
