import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { LevelAnnouncementGroup, TemplateToken } from '../../../models/level-up-announcement.models';
import { AnnouncementEditorDiagnostic, parseAnnouncementList, ParsedAnnouncementDocument } from './announcement-list-parser';
import { reconcileAnnouncementGroups } from './announcement-list-reconciler';
import { serializeAnnouncementGroups } from './announcement-list-serializer';

export interface AnnouncementEditorSelection { groupIndex: number; messageIndex: number | null; line: number; }

@Component({
  selector: 'announcement-list-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslocoPipe],
  templateUrl: './announcement-list-editor.component.html',
  styleUrl: './announcement-list-editor.component.scss',
})
export class AnnouncementListEditorComponent implements OnChanges {
  @Input() groups: LevelAnnouncementGroup[] = [];
  @Input() text = '';
  @Input() availableTokens: TemplateToken[] = [];
  @Input() scope: 'Lifetime' | 'Season' = 'Lifetime';
  @Input() kind: 'LevelUp' | 'Reward' = 'LevelUp';
  @Output() readonly groupsChange = new EventEmitter<LevelAnnouncementGroup[]>();
  @Output() readonly textChange = new EventEmitter<string>();
  @Output() readonly dirtyChange = new EventEmitter<void>();
  @Output() readonly diagnosticsChange = new EventEmitter<AnnouncementEditorDiagnostic[]>();
  @Output() readonly selectionChange = new EventEmitter<AnnouncementEditorSelection>();

  value = '';
  parsed: ParsedAnnouncementDocument = { groups: [], diagnostics: [] };
  activeLine = 1;
  selectionStart = 0;
  selectionEnd = 0;
  private parseTimer: ReturnType<typeof setTimeout> | undefined;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['text'] && changes['text'].currentValue !== this.value) {
      this.value = this.text;
      this.parse(false);
    } else if (changes['availableTokens'] || changes['scope'] || changes['kind']) {
      this.parse(false);
    }
  }

  get lines(): string[] { return this.value.split('\n'); }
  get activeText(): string { return this.lines[this.activeLine - 1]?.trim() ?? ''; }
  get activeCharacters(): number { return this.activeText.replace(/^\\?\/\//, '').replace(/^\/\/\s*@off\s+/i, '').length; }
  tokenLabel(token: TemplateToken): string { return `{${token.name}}`; }
  get errors(): AnnouncementEditorDiagnostic[] { return this.parsed.diagnostics.filter(diagnostic => diagnostic.severity === 'error'); }
  get warnings(): AnnouncementEditorDiagnostic[] { return this.parsed.diagnostics.filter(diagnostic => diagnostic.severity === 'warning'); }
  get lineNumbers(): number[] { return this.lines.map((_, index) => index + 1); }

  onInput(value: string): void {
    this.value = value;
    this.dirtyChange.emit();
    this.textChange.emit(value);
    if (this.parseTimer) clearTimeout(this.parseTimer);
    this.parseTimer = setTimeout(() => this.parse(true), 200);
  }

  onCursor(textarea: HTMLTextAreaElement): void {
    this.selectionStart = textarea.selectionStart;
    this.selectionEnd = textarea.selectionEnd;
    this.activeLine = this.value.slice(0, this.selectionStart).split('\n').length;
    this.emitSelection();
  }

  insertToken(token: TemplateToken, textarea: HTMLTextAreaElement): void {
    const line = this.lines[this.activeLine - 1]?.trim() ?? '';
    if (line.startsWith('//') && !line.startsWith('\\//')) return;
    const start = this.selectionStart;
    const end = this.selectionEnd;
    const insertion = `{${token.name}}`;
    this.onInput(this.value.slice(0, start) + insertion + this.value.slice(end));
    setTimeout(() => {
      textarea.focus();
      textarea.selectionStart = textarea.selectionEnd = start + insertion.length;
      this.selectionStart = this.selectionEnd = start + insertion.length;
    });
  }

  normalize(): void {
    if (this.errors.length) return;
    this.onInput(serializeAnnouncementGroups(this.groups));
  }

  private parse(emitGroups: boolean): void {
    this.parsed = parseAnnouncementList(this.value, { availableTokens: this.availableTokens, scope: this.scope, kind: this.kind });
    this.diagnosticsChange.emit(this.parsed.diagnostics);
    if (emitGroups && !this.parsed.diagnostics.some(diagnostic => diagnostic.severity === 'error')) {
      this.groupsChange.emit(reconcileAnnouncementGroups(this.parsed, this.groups, () => crypto.randomUUID().replaceAll('-', '')));
    }
    this.emitSelection();
  }

  private emitSelection(): void {
    const groupIndex = this.parsed.groups.reduce((selected, group, index) => group.sourceLine <= this.activeLine ? index : selected, 0);
    const group = this.parsed.groups[groupIndex];
    const messageIndex = group?.messages.findIndex(message => message.sourceLine === this.activeLine) ?? -1;
    this.selectionChange.emit({ groupIndex, messageIndex: messageIndex >= 0 ? messageIndex : null, line: this.activeLine });
  }
}
