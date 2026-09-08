import { ChangeDetectorRef, Component, DestroyRef, ElementRef, HostListener, Input, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';

export interface ServiceNavItem {
  path: string;
  label: string;
  group: string;
  visible?: boolean;
}

@Component({
  selector: 'app-service-shell',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './service-shell.component.html',
  styleUrls: ['./service-shell.component.css'],
})
export class ServiceShellComponent {
  @Input() chrome = true;
  @Input() serviceName = '';
  @Input() serviceDescription = '';
  @Input() links: ServiceNavItem[] = [];
  @ViewChild('drawer') drawer?: ElementRef<HTMLElement>;
  @ViewChild('menuButton') menuButton?: ElementRef<HTMLButtonElement>;
  @ViewChild('mainContent') mainContent?: ElementRef<HTMLElement>;
  private readonly router = inject(Router);
  private readonly changeDetector = inject(ChangeDetectorRef);
  private readonly destroyRef = inject(DestroyRef);
  mobile = typeof window !== 'undefined' && window.innerWidth < 960;
  menuOpen = false;
  currentPath = this.router.url.split(/[?#]/)[0];

  constructor() {
    this.router.events.pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd), takeUntilDestroyed(this.destroyRef))
      .subscribe(event => {
        this.currentPath = event.urlAfterRedirects.split(/[?#]/)[0];
        this.closeMenu(false);
        requestAnimationFrame(() => this.mainContent?.nativeElement.focus({ preventScroll: true }));
      });
  }

  get visibleLinks(): ServiceNavItem[] { return this.links.filter(link => link.visible !== false); }
  get groups(): string[] { return [...new Set(this.visibleLinks.map(link => link.group))]; }
  trackLink(_index: number, link: ServiceNavItem): string { return link.path; }
  linksIn(group: string): ServiceNavItem[] { return this.visibleLinks.filter(link => link.group === group); }
  get homePath(): string { return this.visibleLinks[0]?.path ?? '/'; }
  isCurrent(link: ServiceNavItem): boolean { return this.currentLink?.path === link.path; }
  get currentLabel(): string { return this.currentLink?.label ?? this.serviceName; }
  get currentLink(): ServiceNavItem | undefined {
    return [...this.visibleLinks].sort((a, b) => b.path.length - a.path.length)
      .find(link => this.currentPath === link.path || this.currentPath.startsWith(link.path + '/'));
  }

  openMenu(): void {
    this.menuOpen = true;
    this.changeDetector.detectChanges();
    this.drawer?.nativeElement.querySelector<HTMLButtonElement>('button')?.focus();
  }
  closeMenu(restoreFocus = true): void {
    const wasOpen = this.menuOpen;
    this.menuOpen = false;
    if (wasOpen) this.changeDetector.detectChanges();
    if (wasOpen && restoreFocus) this.menuButton?.nativeElement.focus();
  }
  @HostListener('window:resize') onResize(): void {
    this.mobile = window.innerWidth < 960;
    if (!this.mobile) this.closeMenu(false);
  }
  @HostListener('document:keydown', ['$event']) onKeydown(event: KeyboardEvent): void {
    if (!this.mobile || !this.menuOpen) return;
    if (event.key === 'Escape') { event.preventDefault(); this.closeMenu(); return; }
    if (event.key !== 'Tab') return;
    const focusable = Array.from(this.drawer?.nativeElement.querySelectorAll<HTMLElement>('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex="0"]') ?? [])
      .filter(element => element.getClientRects().length > 0);
    const first = focusable[0], last = focusable[focusable.length - 1];
    if (!first) return;
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  }
}
