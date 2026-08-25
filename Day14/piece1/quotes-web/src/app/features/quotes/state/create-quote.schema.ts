import { ValidationError, required, schema, validate } from '@angular/forms/signals';

import {
  QUOTE_AUTHOR_MAX_LENGTH,
  QUOTE_TEXT_MAX_LENGTH,
  isBlank,
  serverLengthOf,
} from '../domain/create-quote';

/** What the two inputs are bound to. Mirrors `CreateQuoteRequest` exactly — no extra fields. */
export interface CreateQuoteModel {
  author: string;
  text: string;
}

export const EMPTY_CREATE_QUOTE: CreateQuoteModel = { author: '', text: '' };

/**
 * Validation, kept deliberately in step with `Quote.Create` on the server.
 *
 * Each rule below has a counterpart in Models/Quote.cs. The point is not to duplicate the
 * server — it still decides — but to fail fast on input the server is certain to reject,
 * and to say why in the same terms.
 *
 * Note the separate blank check. `required()` is satisfied by `"   "`, whereas the server
 * uses `string.IsNullOrWhiteSpace`, so without it a spaces-only author sails past the form
 * and comes back as a 400.
 */
/**
 * Length rule, written by hand instead of with Signal Forms' `maxLength()`.
 *
 * `maxLength()` also stamps a native `maxlength` attribute on the control, and the browser
 * then silently truncates anything longer — paste 250 characters into a 200-limit field and
 * 50 disappear with no error, no `aria-invalid`, and nothing announced. That both destroys
 * input and makes the "too long" error unreachable. Validating by hand keeps the attribute
 * off, so over-typing is allowed, visible, and reported. See the note in create-quote.ts.
 */
function withinLength(
  limit: number,
): (context: { value: () => string }) => ValidationError | undefined {
  return ({ value }) => {
    const overBy = serverLengthOf(value()) - limit;
    return overBy > 0
      ? { kind: 'maxlength', message: `Must be ${limit} characters or fewer. Remove ${overBy}.` }
      : undefined;
  };
}

/**
 * Whitespace-only, but *not* empty.
 *
 * `required()` already covers the empty string. Without the `!== ''` guard both validators
 * fire at once on an untouched field and the same sentence is listed twice — once in the
 * summary and once inline — which a screen reader dutifully reads out twice.
 */
function notWhitespaceOnly(
  message: string,
): (context: { value: () => string }) => ValidationError | undefined {
  return ({ value }) =>
    value() !== '' && isBlank(value()) ? { kind: 'blank', message } : undefined;
}

export const createQuoteSchema = schema<CreateQuoteModel>((path) => {
  required(path.author, { message: 'Enter an author to credit.' });
  validate(path.author, notWhitespaceOnly('Enter an author to credit.'));
  validate(path.author, withinLength(QUOTE_AUTHOR_MAX_LENGTH));

  required(path.text, { message: 'Enter the quote text.' });
  validate(path.text, notWhitespaceOnly('Enter the quote text.'));
  validate(path.text, withinLength(QUOTE_TEXT_MAX_LENGTH));
});

/** Characters still available before the server would reject the value. */
export function remainingCharacters(value: string, limit: number): number {
  return limit - serverLengthOf(value);
}
