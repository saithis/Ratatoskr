import { Injectable, signal, effect, inject, PLATFORM_ID } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';

const STORAGE_KEY = 'ratatoskr-theme';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly platformId = inject(PLATFORM_ID);

  readonly isDark = signal<boolean>(this.loadPreference());

  constructor() {
    effect(() => {
      this.applyTheme(this.isDark());
      this.savePreference(this.isDark());
    });
  }

  toggle(): void {
    this.isDark.update(d => !d);
  }

  private loadPreference(): boolean {
    if (!isPlatformBrowser(this.platformId)) return false;
    const stored = localStorage.getItem(STORAGE_KEY);
    return stored === 'dark';
  }

  private savePreference(dark: boolean): void {
    if (!isPlatformBrowser(this.platformId)) return;
    localStorage.setItem(STORAGE_KEY, dark ? 'dark' : 'light');
  }

  private applyTheme(dark: boolean): void {
    if (!isPlatformBrowser(this.platformId)) return;
    document.body.classList.toggle('dark-theme', dark);
  }
}
