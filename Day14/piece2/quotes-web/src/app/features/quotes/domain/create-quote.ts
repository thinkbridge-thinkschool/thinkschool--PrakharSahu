/**
 * The write side of the Week-1 API.
 *
 * Everything here is copied from the server, not guessed:
 *
 *   POST /api/quotes                      QuoteEndpointExtensions.cs:26
 *   body  CreateQuoteRequest(Author, Text) Dtos/CreateQuoteRequest.cs
 *   rules Quote.Create(...)                Models/Quote.cs:27
 *
 * The endpoint takes exactly two fields. `id`, `createdAt`, `isDeleted` and `userId` come
 * back on the 201 but are assigned server-side — a form that offers them is inventing UI
 * the API will ignore.
 */

/** `author.Length > 200` fails `Quote.Create` — Models/Quote.cs:32. */
export const QUOTE_AUTHOR_MAX_LENGTH = 200;

/** `text.Length > 1000` fails `Quote.Create` — Models/Quote.cs:29. */
export const QUOTE_TEXT_MAX_LENGTH = 1000;

/** Request body for `POST /api/quotes`, camelCased by ASP.NET's default serialiser. */
export interface CreateQuoteRequest {
  readonly author: string;
  readonly text: string;
}

/**
 * The server rejects blank input with `string.IsNullOrWhiteSpace`, so a value of `"   "`
 * is invalid there even though it is a non-empty string here. Plain `required()` would let
 * it through and turn a preventable client-side error into a 400.
 */
export function isBlank(value: string): boolean {
  return value.trim().length === 0;
}

/**
 * These limits are enforced by validation, never by a native `maxlength` attribute.
 *
 * `maxlength` makes the browser drop the excess as it is typed or pasted: the field ends up
 * holding exactly `limit` characters, no error fires, and nothing is announced. The user is
 * not told their input was cut. Allowing the value to exceed the limit and then saying so
 * is the accessible behaviour, and it is also the only way the message below is ever seen.
 */

/**
 * Length as the server counts it.
 *
 * C#'s `string.Length` counts UTF-16 code units, and so does JavaScript's — so an emoji
 * outside the BMP costs 2 on both sides and the two limits genuinely agree. Named rather
 * than inlined so that assumption is visible and testable.
 */
export function serverLengthOf(value: string): number {
  return value.length;
}
