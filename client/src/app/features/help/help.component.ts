// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../../environments/environment';

interface HelpTopicSummary {
  slug: string;
  title: string;
  order: number;
  tags: string[];
}

interface HelpTopic extends HelpTopicSummary {
  markdown: string;
}

@Component({
  selector: 'app-help',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="help-layout">
      <aside class="help-sidebar">
        <h2>Help Topics</h2>
        <input
          class="search-input"
          placeholder="Search help..."
          (input)="onSearch($event)"
        />
        <nav>
          <a
            *ngFor="let topic of topics"
            [class.active]="selectedSlug === topic.slug"
            (click)="selectTopic(topic.slug)"
          >
            {{ topic.title }}
          </a>
        </nav>
        <div class="help-meta">
          <p>
            <a [href]="swaggerUrl" target="_blank">Swagger API Docs</a>
          </p>
          <p class="copyright">Copyright &copy; Rivoli AI 2026</p>
        </div>
      </aside>

      <main class="help-content">
        <div *ngIf="loading" class="loading">Loading...</div>
        <div *ngIf="error" class="error">{{ error }}</div>
        <div
          *ngIf="currentTopic && !loading"
          class="markdown-body"
          [innerHTML]="renderedHtml"
        ></div>
        <div *ngIf="!currentTopic && !loading && !error" class="empty">
          Select a topic from the sidebar.
        </div>
      </main>
    </div>
  `,
  styles: [`
    .help-layout { display: flex; gap: 0; min-height: calc(100vh - 100px); margin: -24px; }

    .help-sidebar {
      width: 260px; min-width: 260px;
      background: var(--surface); border-right: 1px solid var(--border);
      padding: 24px 16px; display: flex; flex-direction: column;
    }
    .help-sidebar h2 { font-size: 16px; margin-bottom: 12px; }
    .search-input {
      width: 100%; padding: 8px 12px; border: 1px solid var(--border);
      border-radius: 4px; font-size: 13px; margin-bottom: 12px;
    }
    .help-sidebar nav { flex: 1; display: flex; flex-direction: column; gap: 2px; }
    .help-sidebar nav a {
      padding: 8px 12px; border-radius: 4px; font-size: 14px; cursor: pointer;
      color: var(--text-secondary);
    }
    .help-sidebar nav a:hover { background: var(--background); }
    .help-sidebar nav a.active { color: var(--primary); background: rgba(26,115,232,0.08); font-weight: 500; }
    .help-meta { margin-top: auto; padding-top: 16px; font-size: 12px; color: var(--text-secondary); }
    .help-meta a { font-size: 12px; }
    .copyright { margin-top: 8px; }

    .help-content { flex: 1; padding: 32px 40px; overflow-y: auto; }
    .loading, .error, .empty { color: var(--text-secondary); font-size: 14px; padding: 24px; }
    .error { color: var(--error); }

    .markdown-body { font-size: 14px; line-height: 1.7; }
    .markdown-body :first-child { margin-top: 0; }
    .markdown-body h1 { font-size: 24px; margin: 0 0 16px; }
    .markdown-body h2 { font-size: 18px; margin: 24px 0 12px; padding-bottom: 6px; border-bottom: 1px solid var(--border); }
    .markdown-body h3 { font-size: 15px; margin: 20px 0 8px; }
    .markdown-body p { margin: 8px 0; }
    .markdown-body code {
      background: var(--background); padding: 2px 6px; border-radius: 3px; font-size: 13px;
    }
    .markdown-body pre {
      background: var(--background); padding: 16px; border-radius: 6px;
      overflow-x: auto; margin: 12px 0;
    }
    .markdown-body pre code { background: none; padding: 0; }
    .markdown-body table { width: 100%; border-collapse: collapse; margin: 12px 0; }
    .markdown-body th, .markdown-body td {
      padding: 8px 12px; border: 1px solid var(--border); text-align: left; font-size: 13px;
    }
    .markdown-body th { background: var(--background); font-weight: 600; }
    .markdown-body ul, .markdown-body ol { padding-left: 24px; }
    .markdown-body li { margin: 4px 0; }
    .markdown-body blockquote {
      border-left: 3px solid var(--primary); margin: 12px 0; padding: 8px 16px;
      color: var(--text-secondary); background: rgba(26,115,232,0.04);
    }
    .markdown-body a { color: var(--primary); }
    .markdown-body strong { font-weight: 600; }
  `],
})
export class HelpComponent implements OnInit {
  topics: HelpTopicSummary[] = [];
  currentTopic: HelpTopic | null = null;
  selectedSlug = '';
  renderedHtml = '';
  loading = false;
  error = '';
  swaggerUrl = '/swagger';

  private apiBase = environment.apiUrl;

  constructor(private http: HttpClient) {}

  ngOnInit(): void {
    this.loadTopics();
  }

  loadTopics(): void {
    this.http.get<HelpTopicSummary[]>(`${this.apiBase}/help/topics`).subscribe({
      next: (topics) => {
        this.topics = topics;
        if (topics.length > 0) {
          this.selectTopic(topics[0].slug);
        }
      },
      error: () => {
        this.error = 'Could not load help topics.';
      },
    });
  }

  selectTopic(slug: string): void {
    this.selectedSlug = slug;
    this.loading = true;
    this.error = '';
    this.http.get<HelpTopic>(`${this.apiBase}/help/topics/${slug}`).subscribe({
      next: (topic) => {
        this.currentTopic = topic;
        this.renderedHtml = this.renderMarkdown(topic.markdown);
        this.loading = false;
      },
      error: () => {
        this.error = `Could not load topic: ${slug}`;
        this.loading = false;
      },
    });
  }

  onSearch(event: Event): void {
    const query = (event.target as HTMLInputElement).value;
    if (!query.trim()) {
      this.loadTopics();
      return;
    }
    this.http.get<HelpTopicSummary[]>(`${this.apiBase}/help/search`, { params: { q: query } }).subscribe({
      next: (topics) => {
        this.topics = topics;
      },
    });
  }

  /**
   * Minimal markdown-to-HTML renderer.
   * For production, consider using 'marked' or 'ngx-markdown'.
   */
  private renderMarkdown(md: string): string {
    let html = md
      // Code blocks
      .replace(/```(\w*)\n([\s\S]*?)```/g, '<pre><code>$2</code></pre>')
      // Inline code
      .replace(/`([^`]+)`/g, '<code>$1</code>')
      // Headers
      .replace(/^### (.+)$/gm, '<h3>$1</h3>')
      .replace(/^## (.+)$/gm, '<h2>$1</h2>')
      .replace(/^# (.+)$/gm, '<h1>$1</h1>')
      // Bold
      .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
      // Links
      .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2" target="_blank">$1</a>')
      // Blockquotes
      .replace(/^> (.+)$/gm, '<blockquote>$1</blockquote>')
      // Tables (basic: | col | col |)
      .replace(/^\|(.+)\|$/gm, (match) => {
        const cells = match.split('|').filter(c => c.trim()).map(c => c.trim());
        if (cells.every(c => /^[-:]+$/.test(c))) return '';
        const tag = match.includes('---') ? 'th' : 'td';
        return '<tr>' + cells.map(c => `<${tag}>${c}</${tag}>`).join('') + '</tr>';
      })
      // Line breaks
      .replace(/\n\n/g, '</p><p>')
      // Unordered lists
      .replace(/^- (.+)$/gm, '<li>$1</li>');

    // Wrap <li> runs in <ul>
    html = html.replace(/(<li>[\s\S]*?<\/li>\n?)+/g, '<ul>$&</ul>');
    // Wrap <tr> runs in <table>
    html = html.replace(/(<tr>[\s\S]*?<\/tr>\n?)+/g, '<table>$&</table>');

    return `<p>${html}</p>`.replace(/<p><\/p>/g, '').replace(/<p>\s*<\/p>/g, '');
  }
}
