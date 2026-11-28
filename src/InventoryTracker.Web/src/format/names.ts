/**
 * Names with their ids (F-10, design 12.8; AC-37.4; SEC-09, SEC-25, SEC-27).
 *
 * Two customers can look the same: "Deli Norte" (#4), and the same name with a Cyrillic "o" (U+043E) in place of the
 * Latin one (#9). Showing the id beside every customer name, and beside item names in pickers, on Stock and on the
 * Items screens, lets the user tell them apart. Order lines and dialogs name items without the id (PRD row 72).
 *
 * The functions here give the label as plain text, for places that hold only text: the document title, a dialog's
 * accessible name. On the page itself, use `NameWithId` (src/components/SafeText.tsx), which keeps the name in its own
 * direction-isolated element so a right-to-left name can never move the id (design 12.8, "Isolation").
 */
import { showDirectionCharacters } from './direction';

/** F-10: the id as it follows a name, "(#4)". */
export function idText(id: number): string {
  return `(#${id})`;
}

/** F-10, design 12.8: what follows a deactivated customer, after its id. */
export const inactiveText = ' (inactive)';

/** A customer as it appears inside other records and in the customer list (design 2.3, 5.11). */
export interface CustomerName {
  readonly id: number;
  readonly name: string;
  readonly isActive: boolean;
}

/**
 * F-10: "Deli Norte (#4)", or "Cafe Sur (#7) (inactive)" when `isActive` is false. Direction characters in the name
 * are shown as markers (AC-37.3).
 */
export function customerLabel(customer: CustomerName): string {
  return `${showDirectionCharacters(customer.name)} ${idText(customer.id)}${customer.isActive ? '' : inactiveText}`;
}

/** F-10: "Cheese (#3)". Direction characters in the name are shown as markers (AC-37.3). */
export function itemLabel(name: string, id: number): string {
  return `${showDirectionCharacters(name)} ${idText(id)}`;
}
