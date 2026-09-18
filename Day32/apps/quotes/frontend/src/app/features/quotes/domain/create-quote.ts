/**
 * The write side of the Week-1 API.
 *
 *   POST /api/quotes                        QuoteEndpointExtensions.cs:26
 *   body  CreateQuoteRequest(Author, Text)  Dtos/CreateQuoteRequest.cs
 *   rules Quote.Create(...)                 Models/Quote.cs:27
 *
 * Exactly two fields. `id`, `createdAt`, `isDeleted` and `userId` are assigned server-side
 * and come back on the 201.
 */

/** `author.Length > 200` fails `Quote.Create` — Models/Quote.cs:32. */
export const QUOTE_AUTHOR_MAX_LENGTH = 200;

/** `text.Length > 1000` fails `Quote.Create` — Models/Quote.cs:29. */
export const QUOTE_TEXT_MAX_LENGTH = 1000;

export interface CreateQuoteRequest {
  readonly author: string;
  readonly text: string;
}

/**
 * The server rejects blank input with `string.IsNullOrWhiteSpace`, so `"   "` is invalid
 * there even though it is a non-empty string here. Plain `required()` would let it through
 * and turn a preventable client-side error into a 400 — confirmed against the live API,
 * which answers `{"message":"Text must be between 1 and 1000 characters."}`.
 */
export function isBlank(value: string): boolean {
  return value.trim().length === 0;
}

/** Length as the server counts it: UTF-16 code units, same as C#'s `string.Length`. */
export function serverLengthOf(value: string): number {
  return value.length;
}

/**
 * Punctuation the server tolerates, spelled out for error messages and hints.
 *
 * This is the mirror of `TextRules.AllowedPunctuationHint` in
 * `Day5/piece6/QuotesApi/Models/TextRules.cs` — change one and change the other, or the
 * form starts disagreeing with the 400 it gets back.
 */
export const ALLOWED_PUNCTUATION_HINT = `. , ' " - ? ( )`;

/**
 * Mirror of `TextRules.DisallowedPattern()`: anything that is not a letter, a digit,
 * whitespace, or one of the marks above. `\p{L}`/`\p{N}` need the `u` flag, and match the
 * same Unicode categories .NET does, so "Brontë" passes on both sides while "!@#$" fails
 * on both. `!` is intentionally *not* allowed.
 */
const DISALLOWED_CHARACTER = /[^\p{L}\p{N}\s.,'"\-?()]/gu;

/** The offending characters, distinct and in first-seen order. `[]` means the value is clean. */
export function disallowedCharactersIn(value: string): string[] {
  return [...new Set(value.match(DISALLOWED_CHARACTER) ?? [])];
}
