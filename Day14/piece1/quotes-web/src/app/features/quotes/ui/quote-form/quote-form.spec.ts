import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import axe from 'axe-core';

import { AccessTokenStore } from '../../../../core/auth/access-token.store';
import { provideQuotesApiBaseUrl } from '../../../../core/config/quotes-api.config';
import { QUOTE_AUTHOR_MAX_LENGTH, QUOTE_TEXT_MAX_LENGTH } from '../../domain/create-quote';
import { QuoteForm } from './quote-form';

const AUTHOR_ID = 'create-quote-author';
const TEXT_ID = 'create-quote-text';

describe('QuoteForm accessibility and states', () => {
  let fixture: ComponentFixture<QuoteForm>;
  let httpTesting: HttpTestingController;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function authorInput(): HTMLInputElement {
    return root().querySelector<HTMLInputElement>(`#${AUTHOR_ID}`)!;
  }

  function textInput(): HTMLTextAreaElement {
    return root().querySelector<HTMLTextAreaElement>(`#${TEXT_ID}`)!;
  }

  function submitButton(): HTMLButtonElement {
    return root().querySelector<HTMLButtonElement>('button[type="submit"]')!;
  }

  /**
   * Lets pending microtasks run and re-renders. Deliberately not `fixture.whenStable()`:
   * that waits for in-flight HTTP too, so it would deadlock in the tests that need to
   * inspect the DOM *while* the POST is still open.
   */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    TestBed.tick();
  }

  async function type(element: HTMLInputElement | HTMLTextAreaElement, value: string) {
    element.value = value;
    element.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
  }

  /** Fires submit and returns once the handler has had a turn, without awaiting the POST. */
  async function submitForm(): Promise<void> {
    root()
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [QuoteForm],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideQuotesApiBaseUrl('/api')],
    }).compileComponents();

    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(QuoteForm);
    await fixture.whenStable();
  });

  afterEach(() => httpTesting.verify());

  describe('labelling', () => {
    it('associates every control with a real label element', () => {
      for (const id of [AUTHOR_ID, TEXT_ID]) {
        const label = root().querySelector<HTMLLabelElement>(`label[for="${id}"]`);
        expect(label, `no <label for="${id}">`).toBeTruthy();
        expect(root().querySelector(`#${id}`)).toBeTruthy();
      }
    });

    it('uses no placeholder as a substitute for a label', () => {
      expect(authorInput().getAttribute('placeholder')).toBeNull();
      expect(textInput().getAttribute('placeholder')).toBeNull();
    });
  });

  describe('empty state', () => {
    it('shows no errors and marks nothing invalid before the user acts', () => {
      expect(root().querySelector('[role="alert"]')).toBeNull();
      expect(authorInput().getAttribute('aria-invalid')).toBeNull();
      expect(textInput().getAttribute('aria-invalid')).toBeNull();
    });

    it('never points aria-describedby at an element that is not rendered', () => {
      // A dangling idref is silently ignored by assistive tech: the description that was
      // supposed to explain the field reads as nothing at all.
      for (const control of [authorInput(), textInput()]) {
        const ids = (control.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
        expect(ids.length).toBeGreaterThan(0);
        for (const id of ids) {
          expect(root().querySelector(`#${id}`), `dangling idref: ${id}`).toBeTruthy();
        }
      }
    });
  });

  describe('invalid state', () => {
    it('reports both empty fields on submit, in the summary and inline', async () => {
      await submitForm();

      const summary = root().querySelector('[role="alert"]')!;
      expect(summary).toBeTruthy();
      expect(summary.textContent).toContain('Author');
      expect(summary.textContent).toContain('Quote text');

      expect(authorInput().getAttribute('aria-invalid')).toBe('true');
      expect(textInput().getAttribute('aria-invalid')).toBe('true');
      expect(root().querySelector(`#${AUTHOR_ID}-error`)).toBeTruthy();
      expect(root().querySelector(`#${TEXT_ID}-error`)).toBeTruthy();
    });

    it('links the error text into aria-describedby once it appears', async () => {
      await submitForm();

      const described = authorInput().getAttribute('aria-describedby') ?? '';
      expect(described.split(/\s+/)).toContain(`${AUTHOR_ID}-error`);
      // and still resolves to real elements
      for (const id of described.split(/\s+/).filter(Boolean)) {
        expect(root().querySelector(`#${id}`)).toBeTruthy();
      }
    });

    it('moves focus to the first invalid control, in DOM order', async () => {
      await submitForm();
      expect(document.activeElement).toBe(authorInput());
    });

    it('moves focus to the next invalid control when the first is fixed', async () => {
      await type(authorInput(), 'Ada Lovelace');
      await submitForm();
      expect(document.activeElement).toBe(textInput());
    });

    it('rejects whitespace-only input the way the server does', async () => {
      // string.IsNullOrWhiteSpace on the server; plain required() would accept these.
      await type(authorInput(), '   ');
      await type(textInput(), '\t\n ');
      await submitForm();

      expect(root().querySelector(`#${AUTHOR_ID}-error`)?.textContent).toContain('Enter an author');
      expect(root().querySelector(`#${TEXT_ID}-error`)?.textContent).toContain('Enter the quote text');
      httpTesting.expectNone(() => true);
    });

    it('does not send a request while the form is invalid', async () => {
      await submitForm();
      httpTesting.expectNone(() => true);
    });
  });

  describe('submitting and server outcomes', () => {
    async function fillValid() {
      await type(authorInput(), 'Ada Lovelace');
      await type(textInput(), 'Prefer CTEs to correlated subqueries.');
    }

    it('posts author and text, trimmed, to /api/quotes', async () => {
      await type(authorInput(), '  Ada Lovelace  ');
      await type(textInput(), '  Ship it.  ');
      await submitForm();

      const request = httpTesting.expectOne('/api/quotes');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ author: 'Ada Lovelace', text: 'Ship it.' });

      request.flush(
        {
          id: 42,
          text: 'Ship it.',
          author: 'Ada Lovelace',
          createdAt: '2026-08-25T09:00:00+00:00',
          isDeleted: false,
          userId: 1,
        },
        { status: 201, statusText: 'Created' },
      );
      await settle();

      expect(root().querySelector('[data-testid="create-success"]')).toBeTruthy();
    });

    it('marks the submit button busy without removing it from the tab order', async () => {
      await fillValid();
      await submitForm();

      expect(submitButton().getAttribute('aria-disabled')).toBe('true');
      // A `disabled` button is not focusable; aria-disabled keeps it reachable.
      expect(submitButton().hasAttribute('disabled')).toBe(false);

      httpTesting.expectOne('/api/quotes').flush(
        {
          id: 1, text: 'x', author: 'y',
          createdAt: '2026-08-25T09:00:00+00:00', isDeleted: false, userId: 1,
        },
        { status: 201, statusText: 'Created' },
      );
      await settle();
    });

    it('clears the form after a successful create', async () => {
      await fillValid();
      await submitForm();
      httpTesting.expectOne('/api/quotes').flush(
        {
          id: 7, text: 'x', author: 'y',
          createdAt: '2026-08-25T09:00:00+00:00', isDeleted: false, userId: 1,
        },
        { status: 201, statusText: 'Created' },
      );
      await settle();

      expect(authorInput().value).toBe('');
      expect(textInput().value).toBe('');
      expect(root().querySelector(`#${AUTHOR_ID}-error`)).toBeNull();
    });

    it('announces a 401 in an alert region', async () => {
      await fillValid();
      await submitForm();
      httpTesting.expectOne('/api/quotes').flush('', { status: 401, statusText: 'Unauthorized' });
      await settle();

      const alert = root().querySelector('[data-testid="create-unauthenticated"]')!;
      expect(alert).toBeTruthy();
      expect(alert.getAttribute('role')).toBe('alert');
    });

    // Regression: the alert is rendered by an @switch arm that only becomes active during
    // the submit handler, so querying the viewChild inline found nothing and focus was
    // silently left in the last field the user touched.
    it('moves focus onto the server-error alert', async () => {
      await fillValid();
      await submitForm();
      httpTesting.expectOne('/api/quotes').flush('', { status: 401, statusText: 'Unauthorized' });
      await settle();
      await settle();

      expect(document.activeElement).toBe(root().querySelector('[data-testid="create-unauthenticated"]'));
    });

    it('leaves focus alone on success so the next quote can be typed', async () => {
      await fillValid();
      await submitForm();
      httpTesting.expectOne('/api/quotes').flush(
        {
          id: 9, text: 'x', author: 'y',
          createdAt: '2026-08-25T09:00:00+00:00', isDeleted: false, userId: 1,
        },
        { status: 201, statusText: 'Created' },
      );
      await settle();
      await settle();

      const success = root().querySelector('[data-testid="create-success"]')!;
      expect(success.getAttribute('role')).toBe('status');
      expect(document.activeElement).not.toBe(success);
    });

    it('explains a 403 as a missing scope rather than a generic failure', async () => {
      TestBed.inject(AccessTokenStore).set('a-token');
      await fillValid();
      await submitForm();
      httpTesting.expectOne('/api/quotes').flush('', { status: 403, statusText: 'Forbidden' });
      await settle();

      const alert = root().querySelector('[data-testid="create-forbidden"]')!;
      expect(alert.textContent).toContain('quotes.write');
    });

    it("surfaces the server's own wording on a 400", async () => {
      await fillValid();
      await submitForm();
      httpTesting
        .expectOne('/api/quotes')
        .flush(
          { message: 'Author must be between 1 and 200 characters.' },
          { status: 400, statusText: 'Bad Request' },
        );
      await settle();

      expect(root().querySelector('[data-testid="create-rejected-message"]')?.textContent).toContain(
        'Author must be between 1 and 200 characters.',
      );
    });

    it('keeps the typed values when the server rejects them', async () => {
      await fillValid();
      await submitForm();
      httpTesting.expectOne('/api/quotes').flush('', { status: 500, statusText: 'Server Error' });
      await settle();

      expect(authorInput().value).toBe('Ada Lovelace');
      expect(root().querySelector('[data-testid="create-failed"]')).toBeTruthy();
    });
  });

  describe('character limits mirror the server', () => {
    it('uses the limits from Quote.Create', () => {
      expect(QUOTE_AUTHOR_MAX_LENGTH).toBe(200);
      expect(QUOTE_TEXT_MAX_LENGTH).toBe(1000);
    });

    // Regression. Signal Forms' maxLength() stamps a native `maxlength`, and the browser
    // then truncates over-long input silently: 250 pasted characters become 200, with no
    // error and nothing announced. Validating by hand keeps the attribute off.
    it('puts no native maxlength on the controls', () => {
      expect(authorInput().getAttribute('maxlength')).toBeNull();
      expect(textInput().getAttribute('maxlength')).toBeNull();
    });

    it('reports an over-long author instead of discarding characters', async () => {
      await type(authorInput(), 'A'.repeat(QUOTE_AUTHOR_MAX_LENGTH + 50));
      await submitForm();

      expect(authorInput().value.length).toBe(QUOTE_AUTHOR_MAX_LENGTH + 50);
      expect(authorInput().getAttribute('aria-invalid')).toBe('true');
      expect(root().querySelector(`#${AUTHOR_ID}-error`)?.textContent).toContain('Remove 50');
      httpTesting.expectNone(() => true);
    });

    // Regression: required() and the whitespace check both fired on an empty field, so the
    // same sentence was announced twice.
    it('reports an empty field exactly once', async () => {
      await submitForm();

      const inline = root().querySelectorAll(`#${TEXT_ID}-error li`);
      expect(inline.length).toBe(1);

      const summaryItems = [...root().querySelectorAll('[role="alert"] li')].map((li) =>
        li.textContent?.trim(),
      );
      expect(new Set(summaryItems).size).toBe(summaryItems.length);
      expect(summaryItems.length).toBe(2);
    });

    it('reports an over-long quote text', async () => {
      await type(authorInput(), 'Ada Lovelace');
      await type(textInput(), 'x'.repeat(QUOTE_TEXT_MAX_LENGTH + 1));
      await submitForm();

      expect(root().querySelector(`#${TEXT_ID}-error`)?.textContent).toContain('Remove 1');
      httpTesting.expectNone(() => true);
    });

    it('counts surrogate pairs the way C# string.Length does', async () => {
      // An astral-plane emoji is 2 UTF-16 code units in both JS and C#, so the two limits
      // genuinely agree and 100 emoji fill a 200-character field exactly.
      await type(authorInput(), '😀'.repeat(QUOTE_AUTHOR_MAX_LENGTH / 2));
      await submitForm();

      expect(root().querySelector(`#${AUTHOR_ID}-error`)).toBeNull();
    });
  });

  describe('axe', () => {
    it('reports no violations on the empty form', async () => {
      const results = await axe.run(root(), {
        // jsdom has no layout engine, so contrast cannot be computed here; it is checked
        // in the browser instead.
        rules: { 'color-contrast': { enabled: false } },
      });
      expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
    });

    it('reports no violations once errors are displayed', async () => {
      await submitForm();

      const results = await axe.run(root(), {
        rules: { 'color-contrast': { enabled: false } },
      });
      expect(results.violations.map((v) => `${v.id}: ${v.help}`)).toEqual([]);
    });
  });
});
