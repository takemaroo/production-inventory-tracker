/** Design 18.1: the product's name, in the header and on the sign-in screen. */
export const productName = 'Production & Inventory Tracker';

// Design 12.2: the en dash between the page title and the product name. Built from its code point, so no tool that
// copies this file can change it into another dash without the tests seeing it.
const titleSeparator = ` ${String.fromCodePoint(0x2013)} `;

/**
 * Design 12.2 (PRD section 18: each screen has its own title): the browser tab's title for a screen,
 * "{page title} - Production & Inventory Tracker" with an en dash (U+2013) for the "-", for example "Sign in" then
 * the dash and the product name. Screens put it in React 19's `<title>` element, which React moves into the page's
 * head.
 */
export function documentTitle(pageTitle: string): string {
  return `${pageTitle}${titleSeparator}${productName}`;
}
