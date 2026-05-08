// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { provideMonacoEditor } from 'ngx-monaco-editor-v2';
import {
  ApiService,
  CreatePolicyRequest,
  PolicyDto,
  PolicyVersionDto,
} from '../../shared/services/api.service';
import { PolicyEditorComponent } from './policy-editor.component';

describe('PolicyEditorComponent (P9.2)', () => {
  let fixture: ComponentFixture<PolicyEditorComponent>;
  let component: PolicyEditorComponent;
  let api: jasmine.SpyObj<ApiService>;
  let router: jasmine.SpyObj<Router>;

  const samplePolicy: PolicyDto = {
    id: 'pid-1',
    name: 'no-prod',
    description: 'No prod from drafts',
    createdAt: '2026-04-01T00:00:00Z',
    createdBySubjectId: 'user:alice',
    versionCount: 1,
    activeVersionId: null,
  };

  const sampleDraftVersion: PolicyVersionDto = {
    id: 'vid-1',
    policyId: 'pid-1',
    version: 1,
    state: 'Draft',
    enforcement: 'MUST',
    severity: 'critical',
    scopes: ['prod'],
    summary: 'Initial summary',
    rulesJson: '{ "allow": [] }',
    createdAt: '2026-04-01T00:00:00Z',
    createdBySubjectId: 'user:alice',
    proposerSubjectId: 'user:alice',
  };

  function build(routeParams: Record<string, string> = {}): void {
    api = jasmine.createSpyObj<ApiService>('ApiService', [
      'createPolicy',
      'updatePolicyVersion',
      'getPolicy',
      'getPolicyVersion',
    ]);

    TestBed.configureTestingModule({
      imports: [PolicyEditorComponent],
      providers: [
        provideRouter([]),
        { provide: ApiService, useValue: api },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { paramMap: convertToParamMap(routeParams) },
          },
        },
        // #192 — Monaco's `NGX_MONACO_EDITOR_CONFIG` token is provided
        // app-wide in app.config.ts; specs need their own copy so the
        // editor's DI tree resolves. baseUrl doesn't matter here since
        // Karma doesn't load the AMD chunks (the editor view never
        // actually renders in jsdom — `automaticLayout` no-ops, and
        // ControlValueAccessor still works for form binding).
        provideMonacoEditor({ baseUrl: '/assets/monaco/vs' }),
      ],
    });

    // Spy on the real Router instance after providers are wired so navigate
    // calls are intercepted but Router's own deps still resolve.
    router = TestBed.inject(Router) as unknown as jasmine.SpyObj<Router>;
    spyOn(router, 'navigate').and.returnValue(Promise.resolve(true));
  }

  describe('create mode', () => {
    beforeEach(() => {
      build({});
      fixture = TestBed.createComponent(PolicyEditorComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();
    });

    it('starts in create mode with rules pre-seeded to a JSON object', () => {
      expect(component.mode()).toBe('create');
      const rules = component.form.controls.rulesJson.value;
      expect(JSON.parse(rules)).toEqual({ allow: [], deny: [] });
    });

    it('form is invalid until name + summary are populated', () => {
      expect(component.form.valid).toBeFalse();
      component.form.patchValue({ name: 'no-prod', summary: 'init' });
      expect(component.form.valid).toBeTrue();
    });

    it('rejects invalid rules JSON and disables save', () => {
      component.form.patchValue({ name: 'no-prod', summary: 'init', rulesJson: '{ not json' });
      component.form.controls.rulesJson.markAsTouched();
      fixture.detectChanges();

      expect(component.form.controls.rulesJson.errors?.['json']).toBeTruthy();
      expect(component.canSave()).toBeFalse();
    });

    it('rejects rules JSON that is an array (must be an object)', () => {
      component.form.patchValue({ name: 'no-prod', summary: 'init', rulesJson: '[]' });
      component.form.controls.rulesJson.markAsTouched();

      const err = component.form.controls.rulesJson.errors?.['json'] as string;
      expect(err).toContain('JSON object');
    });

    it('rejects a malformed slug name', () => {
      component.form.patchValue({ name: 'BAD NAME' });
      component.form.controls.name.markAsTouched();

      expect(component.form.controls.name.errors?.['pattern']).toBeTruthy();
    });

    it('save calls createPolicy and navigates to edit on success', () => {
      const created: PolicyVersionDto = { ...sampleDraftVersion };
      api.createPolicy.and.returnValue(of(created));

      component.form.patchValue({
        name: 'no-prod',
        summary: 'Initial',
        scopes: ['prod'],
      });
      component.save();

      expect(api.createPolicy).toHaveBeenCalledTimes(1);
      const req = api.createPolicy.calls.mostRecent().args[0] as CreatePolicyRequest;
      expect(req.name).toBe('no-prod');
      expect(req.summary).toBe('Initial');
      expect(req.scopes).toEqual(['prod']);
      expect(router.navigate).toHaveBeenCalledWith([
        '/policies', 'pid-1', 'versions', 'vid-1', 'edit',
      ]);
    });

    it('surfaces a server error in the banner', () => {
      api.createPolicy.and.returnValue(
        throwError(() => new HttpErrorResponse({ status: 500, error: { title: 'oops' } })),
      );

      component.form.patchValue({ name: 'no-prod', summary: 'Initial' });
      component.save();
      fixture.detectChanges();

      expect(component.errorMessage()).toContain('oops');
      const banner = fixture.nativeElement.querySelector('[data-testid="banner"]');
      expect(banner).toBeTruthy();
    });
  });

  describe('edit mode', () => {
    it('loads version + policy and populates the form', () => {
      build({ id: 'pid-1', vId: 'vid-1' });
      api.getPolicyVersion.and.returnValue(of(sampleDraftVersion));
      api.getPolicy.and.returnValue(of(samplePolicy));

      fixture = TestBed.createComponent(PolicyEditorComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();

      expect(component.mode()).toBe('edit');
      expect(component.form.controls.summary.value).toBe('Initial summary');
      expect(component.form.controls.enforcement.value).toBe('MUST');
      expect(component.form.controls.scopes.value).toEqual(['prod']);
      expect(component.form.controls.name.disabled).toBeTrue();
      expect(component.form.controls.description.disabled).toBeTrue();
    });

    it('redirects with a banner query when the version state is not Draft', () => {
      build({ id: 'pid-1', vId: 'vid-1' });
      const active: PolicyVersionDto = { ...sampleDraftVersion, state: 'Active' };
      api.getPolicyVersion.and.returnValue(of(active));

      fixture = TestBed.createComponent(PolicyEditorComponent);
      fixture.detectChanges();

      expect(router.navigate).toHaveBeenCalledWith(
        ['/policies'],
        { queryParams: { error: 'only-draft-editable' } },
      );
    });

    it('save calls updatePolicyVersion with version-level fields only', () => {
      build({ id: 'pid-1', vId: 'vid-1' });
      api.getPolicyVersion.and.returnValue(of(sampleDraftVersion));
      api.getPolicy.and.returnValue(of(samplePolicy));
      api.updatePolicyVersion.and.returnValue(of({ ...sampleDraftVersion, summary: 'updated' }));

      fixture = TestBed.createComponent(PolicyEditorComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();

      component.form.patchValue({ summary: 'updated' });
      component.save();

      expect(api.updatePolicyVersion).toHaveBeenCalledTimes(1);
      const [pid, vid, req] = api.updatePolicyVersion.calls.mostRecent().args;
      expect(pid).toBe('pid-1');
      expect(vid).toBe('vid-1');
      expect(req.summary).toBe('updated');
      // Update body must NOT carry a name/description (server doesn't accept it).
      expect((req as any).name).toBeUndefined();
      expect((req as any).description).toBeUndefined();
    });
  });

  // #192 — Monaco wiring. The editor itself isn't exercised in jsdom
  // (no AMD loader, no canvas) — we test the `onMonacoEditorInit`
  // callback in isolation against a fake monaco global.
  describe('Monaco schema wiring (#192)', () => {
    let fakeSetDiagnostics: jasmine.Spy;

    beforeEach(() => {
      build();
      api.getPolicy.and.returnValue(of(samplePolicy));
      api.getPolicyVersion.and.returnValue(of(sampleDraftVersion));
      (api as any).getRulesSchema = jasmine.createSpy('getRulesSchema');

      fakeSetDiagnostics = jasmine.createSpy('setDiagnosticsOptions');
      // Stand up a minimal `window.monaco` shape so the callback can
      // call setDiagnosticsOptions without exploding.
      (window as any).monaco = {
        languages: {
          json: {
            jsonDefaults: { setDiagnosticsOptions: fakeSetDiagnostics },
          },
        },
      };

      fixture = TestBed.createComponent(PolicyEditorComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();
    });

    afterEach(() => {
      delete (window as any).monaco;
    });

    it('fetches the schema and feeds it to monaco.languages.json.jsonDefaults', () => {
      const fakeSchema = { type: 'object', additionalProperties: true };
      (api as any).getRulesSchema.and.returnValue(of(fakeSchema));

      component.onMonacoEditorInit();

      expect((api as any).getRulesSchema).toHaveBeenCalledTimes(1);
      expect(fakeSetDiagnostics).toHaveBeenCalledTimes(1);
      const arg = fakeSetDiagnostics.calls.mostRecent().args[0];
      expect(arg.validate).toBeTrue();
      expect(arg.schemas[0].uri).toBe(
        'https://andy-policies/schemas/rules.json');
      expect(arg.schemas[0].schema).toBe(fakeSchema);
    });

    it('schema fetch failure is non-fatal — setDiagnosticsOptions is not called', () => {
      const failure = new HttpErrorResponse({ status: 503 });
      (api as any).getRulesSchema.and.returnValue(throwError(() => failure));

      component.onMonacoEditorInit();

      expect(fakeSetDiagnostics).not.toHaveBeenCalled();
    });

    it('does nothing if monaco global is missing (defensive guard)', () => {
      delete (window as any).monaco;
      const fakeSchema = { type: 'object' };
      (api as any).getRulesSchema.and.returnValue(of(fakeSchema));

      component.onMonacoEditorInit();

      expect((api as any).getRulesSchema).not.toHaveBeenCalled();
    });
  });
});
