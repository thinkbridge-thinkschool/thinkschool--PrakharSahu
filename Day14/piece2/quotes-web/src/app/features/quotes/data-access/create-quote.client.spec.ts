import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { provideQuotesApiBaseUrl } from '../../../core/config/quotes-api.config';
import { CreateQuoteClient } from './create-quote.client';

const CREATED_QUOTE = {
  id: 42,
  text: 'Ship it.',
  author: 'Ada Lovelace',
  createdAt: '2026-08-25T09:00:00+00:00',
  isDeleted: false,
  userId: 1,
};

describe('CreateQuoteClient', () => {
  let client: CreateQuoteClient;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideQuotesApiBaseUrl('/api')],
    });
    client = TestBed.inject(CreateQuoteClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('POSTs exactly the two fields CreateQuoteRequest declares', async () => {
    const outcome = client.create({ author: 'Ada Lovelace', text: 'Ship it.' });

    const request = httpTesting.expectOne('/api/quotes');
    expect(request.request.method).toBe('POST');
    expect(Object.keys(request.request.body as object).sort()).toEqual(['author', 'text']);
    request.flush(CREATED_QUOTE, { status: 201, statusText: 'Created' });

    expect(await outcome).toEqual({ status: 'created', quote: CREATED_QUOTE });
  });

  it('validates the 201 body rather than trusting the cast', async () => {
    const outcome = client.create({ author: 'a', text: 'b' });
    httpTesting.expectOne('/api/quotes').flush({ id: 1 }, { status: 201, statusText: 'Created' });

    expect(await outcome).toEqual({
      status: 'failed',
      message: 'The quote was created but the response was unreadable.',
    });
  });

  it("carries the server's DomainError message through a 400", async () => {
    const outcome = client.create({ author: 'a', text: 'b' });
    httpTesting
      .expectOne('/api/quotes')
      .flush(
        { message: 'Text must be between 1 and 1000 characters.' },
        { status: 400, statusText: 'Bad Request' },
      );

    expect(await outcome).toEqual({
      status: 'rejected',
      message: 'Text must be between 1 and 1000 characters.',
    });
  });

  it('accepts a bare-string 400 body too', async () => {
    const outcome = client.create({ author: 'a', text: 'b' });
    httpTesting
      .expectOne('/api/quotes')
      .flush('Author must be between 1 and 200 characters.', {
        status: 400,
        statusText: 'Bad Request',
      });

    expect(await outcome).toEqual({
      status: 'rejected',
      message: 'Author must be between 1 and 200 characters.',
    });
  });

  it('separates 401 from 403, because they need different fixes', async () => {
    const unauthenticated = client.create({ author: 'a', text: 'b' });
    httpTesting.expectOne('/api/quotes').flush('', { status: 401, statusText: 'Unauthorized' });
    expect(await unauthenticated).toEqual({ status: 'unauthenticated' });

    const forbidden = client.create({ author: 'a', text: 'b' });
    httpTesting.expectOne('/api/quotes').flush('', { status: 403, statusText: 'Forbidden' });
    expect(await forbidden).toEqual({ status: 'forbidden' });
  });

  it('explains an unreachable API rather than reporting status 0', async () => {
    const outcome = client.create({ author: 'a', text: 'b' });
    httpTesting
      .expectOne('/api/quotes')
      .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

    const result = await outcome;
    expect(result.status).toBe('failed');
    expect(result).toHaveProperty('message', expect.stringContaining('Could not reach'));
  });

  it('reports an unexpected status with its code', async () => {
    const outcome = client.create({ author: 'a', text: 'b' });
    httpTesting.expectOne('/api/quotes').flush('', { status: 500, statusText: 'Server Error' });

    expect(await outcome).toEqual({
      status: 'failed',
      message: 'The Quotes API failed with status 500.',
    });
  });
});
