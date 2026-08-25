import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import axe from 'axe-core';

import { provideQuotesApiBaseUrl } from '../../../../core/config/quotes-api.config';
import { QUOTE_AUTHOR_MAX_LENGTH, QUOTE_TEXT_MAX_LENGTH } from '../../domain/create-quote';
import { QuoteFormReactive } from './quote-form-reactive';

const AUTHOR_ID = 'reactive-quote-author';
const TEXT_ID = 'reactive-quote-text';

const CREATED = {
  id: 42,
  text: 'Ship it.',
  author: 'Ada Lovelace',
  createdAt: '2026-08-25T09:00:00+00:00',
  isDeleted: false,
  userId: 1,
};

describe('QuoteFormReactive — same contract as the Signal Forms build', () => {
  let fixture: ComponentFixture<QuoteFormReactive>;
  let httpTesting: HttpTestingController;

  const root = () => fixture.nativeElement as HTMLElement;
  const authorInput = () => root().querySelector<HTMLInputElement>(`#${AUTHOR_ID}`)!;
  const textInput = () => root().querySelector<HTMLTextAreaElement>(`#${TEXT_ID}`)!;
  const submitButton = () => root().querySelector<HTMLButtonElement>('button[type="submit"]')!;
  const readState = (id: string) =>
    root().querySelector(`[data-testid="${id}"]`)?.textContent?.trim();

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    TestBed.tick();
  }

  async function type(element: HTMLInputElement | HTMLTextAreaElement, value: string) {
    element.value = value;
    element.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
  }

  async function blur(element: HTMLElement) {
    element.dispatchEvent(new Event('blur', { bubbles: true }));
    await settle();
  }

  async function submitForm(): Promise<void> {
    root()
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await settle();
  }

  async function fillValid() {
    await type(authorInput(), 'Ada Lovelace');
    await type(textInput(), 'Prefer CTEs to correlated subqueries.');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [QuoteFormReactive],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideQuotesApiBaseUrl('/api')],
    }).compileComponents();

    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(QuoteFormReactive);
    await fixture.whenStable();
  });

  afterEach(() => httpTesting.verify());

  describe('pristine / dirty / touched', () => {
    it('starts pristine, untouched and invalid', () => {
      expect(readState('rx-pristine')).toBe('true');
      expect(readState('rx-dirty')).toBe('false');
      expect(readState('rx-touched')).toBe('false');
      expect(readState('rx-valid')).toBe('false');
    });

    it('becomes dirty on input but stays untouched until blur', async () => {
      await type(authorInput(), 'A');

      expect(readState('rx-dirty')).toBe('true');
      expect(readState('rx-pristine')).toBe('false');
      expect(readState('rx-touched')).toBe('false');
    });

    it('becomes touched on blur without becoming dirty', async () => {
      await blur(authorInput());

      expect(readState('rx-touched')).toBe('true');
      expect(readState('rx-dirty')).toBe('false');
    });

    it('turns valid once both fields satisfy the server rules', async () => {
      await fillValid();
      expect(readState('rx-valid')).toBe('true');
    });

    it('marks everything touched on a failed submit', async () => {
      await submitForm();
      expect(readState('rx-touched')).toBe('true');
    });
  });

  describe('validators fire the same way', () => {
    it('reports both empty fields exactly once each', async () => {
      await submitForm();

      expect(root().querySelectorAll(`#${AUTHOR_ID}-error li`).length).toBe(1);
      expect(root().querySelectorAll(`#${TEXT_ID}-error li`).length).toBe(1);
      expect(root().querySelectorAll('[role="alert"] li').length).toBe(2);
    });

    it('rejects whitespace-only input like the server does', async () => {
      await type(authorInput(), '   ');
      await type(textInput(), '\t\n ');
      await submitForm();

      expect(root().querySelector(`#${AUTHOR_ID}-error`)?.textContent).toContain('Enter an author');
      httpTesting.expectNone(() => true);
    });

    it('puts no native maxlength on the controls', () => {
      // Validators.maxLength is validation-only; only the [maxlength] directive sets the
      // attribute. This is the piece-1 truncation bug that reactive forms never had.
      expect(authorInput().getAttribute('maxlength')).toBeNull();
      expect(textInput().getAttribute('maxlength')).toBeNull();
    });

    it('reports over-long input without discarding characters', async () => {
      await type(authorInput(), 'A'.repeat(QUOTE_AUTHOR_MAX_LENGTH + 50));
      await submitForm();

      expect(authorInput().value.length).toBe(QUOTE_AUTHOR_MAX_LENGTH + 50);
      expect(root().querySelector(`#${AUTHOR_ID}-error`)?.textContent).toContain('Remove 50');
    });

    it('uses the limits from Quote.Create', () => {
      expect(QUOTE_AUTHOR_MAX_LENGTH).toBe(200);
      expect(QUOTE_TEXT_MAX_LENGTH).toBe(1000);
    });
  });

  describe('accessibility parity', () => {
    it('associates every control with a label', () => {
      for (const id of [AUTHOR_ID, TEXT_ID]) {
        expect(root().querySelector(`label[for="${id}"]`)).toBeTruthy();
      }
    });

    it('never points aria-describedby at an element that is not rendered', async () => {
      await submitForm();

      for (const control of [authorInput(), textInput()]) {
        const ids = (control.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
        expect(ids.length).toBeGreaterThan(0);
        for (const id of ids) {
          expect(root().querySelector(`#${id}`), `dangling idref: ${id}`).toBeTruthy();
        }
      }
    });

    it('moves focus to the first invalid control', async () => {
      await submitForm();
      expect(document.activeElement).toBe(authorInput());
    });

    it('moves focus to the next invalid control once the first is fixed', async () => {
      await type(authorInput(), 'Ada Lovelace');
      await submitForm();
      expect(document.activeElement).toBe(textInput());
    });

    it('reports no axe violations, empty and with errors shown', async () => {
      const clean = await axe.run(root(), { rules: { 'color-contrast': { enabled: false } } });
      expect(clean.violations.map((v) => v.id)).toEqual([]);

      await submitForm();
      const withErrors = await axe.run(root(), { rules: { 'color-contrast': { enabled: false } } });
      expect(withErrors.violations.map((v) => v.id)).toEqual([]);
    });
  });

  describe('clean submit', () => {
    it('posts exactly author and text, trimmed', async () => {
      await type(authorInput(), '  Ada Lovelace  ');
      await type(textInput(), '  Ship it.  ');
      await submitForm();

      const request = httpTesting.expectOne('/api/quotes');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ author: 'Ada Lovelace', text: 'Ship it.' });
      request.flush(CREATED, { status: 201, statusText: 'Created' });
      await settle();

      expect(root().querySelector('[data-testid="reactive-create-success"]')).toBeTruthy();
    });

    it('resets value AND state in one call after a create', async () => {
      await fillValid();
      await submitForm();
      httpTesting.expectOne('/api/quotes').flush(CREATED, { status: 201, statusText: 'Created' });
      await settle();

      expect(authorInput().value).toBe('');
      expect(textInput().value).toBe('');
      // FormGroup.reset() clears the value too — Signal Forms' reset() does not.
      expect(readState('rx-pristine')).toBe('true');
      expect(readState('rx-touched')).toBe('false');
    });

    it('keeps the submit button in the tab order while saving', async () => {
      await fillValid();
      await submitForm();

      expect(submitButton().getAttribute('aria-disabled')).toBe('true');
      expect(submitButton().hasAttribute('disabled')).toBe(false);

      httpTesting.expectOne('/api/quotes').flush(CREATED, { status: 201, statusText: 'Created' });
      await settle();
    });
  });

  describe('failed submit', () => {
    it('does not send a request while invalid', async () => {
      await submitForm();
      httpTesting.expectNone(() => true);
    });

    it('announces a 401 and moves focus to the alert', async () => {
      await fillValid();
      await submitForm();
      httpTesting.expectOne('/api/quotes').flush('', { status: 401, statusText: 'Unauthorized' });
      await settle();
      await settle();

      const alert = root().querySelector('[data-testid="reactive-create-unauthenticated"]')!;
      expect(alert.getAttribute('role')).toBe('alert');
      expect(document.activeElement).toBe(alert);
    });

    it("shows the server's own wording on a 400", async () => {
      await fillValid();
      await submitForm();
      httpTesting
        .expectOne('/api/quotes')
        .flush(
          { message: 'Author must be between 1 and 200 characters.' },
          { status: 400, statusText: 'Bad Request' },
        );
      await settle();

      expect(root().querySelector('[data-testid="reactive-create-rejected"]')?.textContent).toContain(
        'Author must be between 1 and 200 characters.',
      );
    });

    it('keeps the typed values when the server fails', async () => {
      await fillValid();
      await submitForm();
      httpTesting.expectOne('/api/quotes').flush('', { status: 500, statusText: 'Server Error' });
      await settle();

      expect(authorInput().value).toBe('Ada Lovelace');
      expect(root().querySelector('[data-testid="reactive-create-failed"]')).toBeTruthy();
    });
  });
});
