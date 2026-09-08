import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
export interface WorkflowLink { path: string; title: string; description: string; }
@Component({
 selector: 'app-page-intro', standalone: true, imports: [CommonModule, RouterLink],
 template: `<header class="intro"><div class="eyebrow">{{ eyebrow }}</div><h1>{{ heading }}</h1><p>{{ description }}</p></header>
 <nav *ngIf="actions.length" class="workflow-links" aria-label="Suggested workflows"><a *ngFor="let action of actions" [routerLink]="action.path"><span class="action-title">{{ action.title }}<span aria-hidden="true">↗</span></span><span class="action-description">{{ action.description }}</span></a></nav>`,
 styles: [`:host{display:block;min-width:0}.intro{margin-bottom:32px}.eyebrow{font:10px/1.5 ui-monospace,monospace;letter-spacing:1.4px;text-transform:uppercase;color:var(--text-secondary,#737373);margin-bottom:12px}h1{font-size:32px;line-height:1.2;letter-spacing:-.8px;font-weight:600;margin:0 0 12px;color:var(--text,#171717)}.intro p{font-size:14px;line-height:1.7;color:var(--text-secondary,#737373);max-width:680px;margin:0}.workflow-links{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(220px,100%),1fr));gap:16px;margin:0 0 32px}.workflow-links a{display:flex;flex-direction:column;gap:10px;padding:20px;border:1px solid var(--border,#e5e5e5);border-radius:12px;background:var(--surface,#fff);text-decoration:none;color:var(--text,#171717)}.workflow-links a:hover{border-color:#087c77}.workflow-links a:focus-visible{outline:2px solid #087c77;outline-offset:3px}.action-title{display:flex;justify-content:space-between;gap:12px;font-size:14px;font-weight:600}.action-title>span{color:#087c77}.action-description{font-size:12px;line-height:1.65;color:var(--text-secondary,#737373)}@media(max-width:600px){h1{font-size:28px}.intro{margin-bottom:24px}}`]
})
export class PageIntroComponent {
 @Input() eyebrow = 'Workspace';
 @Input() heading = '';
 @Input() description = '';
 @Input() actions: WorkflowLink[] = [];
}
