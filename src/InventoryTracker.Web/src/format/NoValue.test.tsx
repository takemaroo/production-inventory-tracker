import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { NoValue, ValueOrNone } from './NoValue';
import { formatSignedPercent } from './numbers';
import { Quantity } from '../components/SafeText';
import type { ApiNumber } from '../api/json';

afterEach(cleanup);

describe('NoValue and ValueOrNone (design 12.7)', () => {
  // Design 12.7: a value not yet known is "-" on screen, with the hidden text "none" for screen readers.
  it('shows "-" to the eye and "none" to a screen reader', () => {
    const { container } = render(<NoValue />);

    const [shown, spoken] = [...container.querySelectorAll('span')];
    expect(shown?.textContent).toBe('-');
    expect(shown?.getAttribute('aria-hidden')).toBe('true');
    expect(spoken?.textContent).toBe('none');
    expect(spoken?.className).toBe('vh');
  });

  // Design 12.7 and 12.8: null shows NoValue; a known quantity shows the Quantity component, its unit a SafeText.
  it('shows the formatted value, or NoValue for null', () => {
    const format = (value: ApiNumber) => <Quantity value={value} unit="kg" />;

    const known = render(<ValueOrNone value={'1015.000'} format={format} />);
    expect(known.container.textContent).toBe('1,015 kg');
    expect(known.container.querySelector('span.num bdi')?.textContent).toBe('kg');
    cleanup();

    const unknown = render(<ValueOrNone<ApiNumber> value={null} format={format} />);
    expect(unknown.container.querySelector('[aria-hidden="true"]')?.textContent).toBe('-');
    expect(unknown.container.textContent).toBe('-none');
  });

  // Design 12.7: a plain string is right for text the front end owns, such as a percentage.
  it('also takes a plain string', () => {
    const { container } = render(<ValueOrNone value={'1.5'} format={formatSignedPercent} />);

    expect(container.textContent).toBe('+1.50%');
  });
});
