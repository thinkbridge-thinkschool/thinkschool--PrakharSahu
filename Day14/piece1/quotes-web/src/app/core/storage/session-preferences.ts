import { Injectable } from '@angular/core';

/**
 * Thin, failure-tolerant wrapper over `sessionStorage`.
 *
 * Storage access throws in some privacy modes and is absent in non-browser runtimes, so
 * every call is guarded: a preference that cannot be read or written is not worth an
 * error boundary. Injectable rather than a bare function so tests can replace it.
 */
@Injectable({ providedIn: 'root' })
export class SessionPreferences {
  read<T>(key: string, fallback: T): T {
    const raw = this.withStorage((storage) => storage.getItem(key));
    if (raw === null || raw === undefined) {
      return fallback;
    }
    try {
      return JSON.parse(raw) as T;
    } catch {
      return fallback;
    }
  }

  write(key: string, value: unknown): void {
    this.withStorage((storage) => storage.setItem(key, JSON.stringify(value)));
  }

  private withStorage<T>(action: (storage: Storage) => T): T | undefined {
    try {
      return typeof sessionStorage === 'undefined' ? undefined : action(sessionStorage);
    } catch {
      return undefined;
    }
  }
}
