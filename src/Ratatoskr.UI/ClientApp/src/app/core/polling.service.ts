import { Injectable, signal, computed, OnDestroy } from '@angular/core';

const STORAGE_KEY = 'ratatoskr-poll-interval';
const DEFAULT_INTERVAL = 30;

@Injectable({ providedIn: 'root' })
export class PollingService implements OnDestroy {
  readonly intervalSeconds = signal<number>(this.loadInterval());
  readonly countdown = signal<number>(this.intervalSeconds());

  private timer?: ReturnType<typeof setInterval>;

  /** Emits each time the countdown reaches zero. Consumers react to this to refetch. */
  private readonly callbacks = new Set<() => void>();

  constructor() {
    this.startTimer();
  }

  ngOnDestroy(): void {
    clearInterval(this.timer);
  }

  onTick(cb: () => void): () => void {
    this.callbacks.add(cb);
    return () => this.callbacks.delete(cb);
  }

  setInterval(seconds: number): void {
    this.intervalSeconds.set(seconds);
    localStorage.setItem(STORAGE_KEY, String(seconds));
    this.countdown.set(seconds);
  }

  reset(): void {
    this.countdown.set(this.intervalSeconds());
  }

  private loadInterval(): number {
    const stored = localStorage.getItem(STORAGE_KEY);
    return stored ? parseInt(stored, 10) : DEFAULT_INTERVAL;
  }

  private startTimer(): void {
    this.timer = setInterval(() => {
      this.countdown.update(n => {
        if (n <= 1) {
          this.callbacks.forEach(cb => cb());
          return this.intervalSeconds();
        }
        return n - 1;
      });
    }, 1000);
  }
}
