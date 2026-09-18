import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';

import {
  Job,
  JobStatus,
  QUOTE_REPORT_JOB,
  SIMULATE_JOB,
  isTerminal,
  simulatePayload,
} from '../../domain/job';
import { JobsStore } from '../../state/jobs-store';

/** One option in the type picker. `hint` explains what the handler on the server actually does. */
interface JobTypeOption {
  readonly value: string;
  readonly label: string;
  readonly hint: string;
}

export const DURATION_FIELD_ID = 'simulate-duration';

/**
 * The jobs route: enqueue work, then watch it happen.
 *
 * NOT behind `authGuard`. `GET /api/jobs` carries no `.RequireAuthorization()`, so guarding
 * the route would hide a list the server hands to anyone — the same mistake the guard's own
 * comment warns about. Only the controls are gated, because only `POST` and `DELETE` need a
 * token.
 *
 * The form is plain signals rather than Signal Forms. There is nothing to validate here that
 * the browser and the server do not already handle: the type comes from a `<select>`, and the
 * duration is a `number` input the server clamps to 0–120,000ms anyway. Reaching for the
 * forms package would add a schema whose every rule is enforced twice elsewhere.
 */
@Component({
  selector: 'app-jobs-page',
  imports: [DatePipe],
  templateUrl: './jobs-page.html',
  styleUrl: './jobs-page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Page-scoped, so the polling timer dies with the view. See the note on the class.
  providers: [JobsStore],
})
export class JobsPage implements OnInit {
  protected readonly store = inject(JobsStore);

  protected readonly durationFieldId = DURATION_FIELD_ID;

  protected readonly jobTypes: readonly JobTypeOption[] = [
    {
      value: QUOTE_REPORT_JOB,
      label: 'Quote report',
      hint: 'Reads every quote and summarises them by author, 400ms per row. The realistic slow job.',
    },
    {
      value: SIMULATE_JOB,
      label: 'Simulated work',
      hint: 'Runs for as long as you ask and fails on request — the quickest way to see each state.',
    },
  ];

  protected readonly type = signal<string>(QUOTE_REPORT_JOB);
  protected readonly durationMs = signal(3000);
  protected readonly shouldFail = signal(false);

  protected readonly isSimulate = computed(() => this.type() === SIMULATE_JOB);

  protected readonly selectedHint = computed(
    () => this.jobTypes.find((option) => option.value === this.type())?.hint ?? '',
  );

  protected readonly accepted = computed(() => {
    const state = this.store.createState();
    return state.kind === 'accepted' ? state.job : null;
  });

  protected readonly submitFailure = computed(() => {
    const state = this.store.createState();
    return state.kind === 'failed' ? state.message : null;
  });

  /**
   * Sentence for the polite live region.
   *
   * A list that rewrites itself every 1.5 seconds is invisible to a screen reader unless
   * something says what changed, and announcing every row would be unusable. The counts are
   * the part worth hearing.
   */
  protected readonly liveSummary = computed(() => {
    const active = this.store.activeCount();
    if (active === 0) {
      return 'No jobs are running.';
    }
    return `${active} job${active === 1 ? '' : 's'} still running. ${this.store.queueDepth()} queued.`;
  });

  /**
   * One fetch per component instance, which then arms the loop.
   *
   * `ngOnInit` rather than `effect()` for the same reason as the quotes list: `load()` reads
   * the state signals it goes on to write, so an effect would depend on what it updates.
   */
  ngOnInit(): void {
    void this.store.load();
  }

  protected onTypeChange(event: Event): void {
    this.type.set((event.target as HTMLSelectElement).value);
  }

  protected onDurationChange(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    // An empty input parses to NaN; fall back rather than sending `{"durationMs":null}`.
    this.durationMs.set(Number.isFinite(value) ? value : 3000);
  }

  protected onShouldFailChange(event: Event): void {
    this.shouldFail.set((event.target as HTMLInputElement).checked);
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();

    await this.store.submit({
      type: this.type(),
      // `quote-report` ignores the payload entirely, so sending one would be noise on the
      // wire and a lie about what the handler reads.
      payload: this.isSimulate()
        ? simulatePayload({ durationMs: this.durationMs(), shouldFail: this.shouldFail() })
        : null,
    });
  }

  protected isActive(job: Job): boolean {
    return !isTerminal(job.status);
  }

  /** Lower-cased status, for the CSS class that colours the chip. */
  protected statusClass(status: JobStatus): string {
    return `status-${status.toLowerCase()}`;
  }

  /** First segment of the GUID. Enough to tell rows apart; the full id is in the `title`. */
  protected shortId(id: string): string {
    return id.slice(0, 8);
  }

  /**
   * Milliseconds as something readable.
   *
   * Sub-second values keep their unit because that is the interesting range for queue
   * latency — "0.1s" hides the difference between a worker keeping up and one that is not.
   */
  protected formatMs(milliseconds: number | null): string {
    if (milliseconds === null) {
      return '—';
    }
    if (milliseconds < 1000) {
      return `${Math.round(milliseconds)}ms`;
    }
    return `${(milliseconds / 1000).toFixed(1)}s`;
  }

  protected formatSeconds(milliseconds: number): string {
    return `${(milliseconds / 1000).toFixed(1)}s`;
  }
}
