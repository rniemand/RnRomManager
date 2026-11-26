<script lang="ts" module>
	/** Release editing B11: the region list, loaded once per page load. */
	let regionList: Promise<string[]> | null = null;
</script>

<script lang="ts">
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import NumberInput from '#lib/components/forms/NumberInput.svelte';
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import { untrack } from 'svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { RomDetail, ReleaseTags } from '#lib/types/index.js';
	import { editReleaseFileName } from '#lib/releaseName.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import BaseModal from './BaseModal.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import type { ReleaseKind } from '#lib/enums/index.js';

	/** Release editing B2–B3: an Admin edits a release's tags; the server renames it to match (B7, B8). */
	interface Props {
		open: boolean;
		gameId: string;
		rom: RomDetail | null;
		/** B8: disc systems keep their file names, so there's no preview; B3: only they have a Disc field. */
		fileSets: boolean;
		/** After a save that changed something: the release's new name and kind. */
		onsaved: (fileName: string, kind: ReleaseKind) => void;
	}

	let { open = $bindable(), gameId, rom, fileSets, onsaved }: Props = $props();

	interface Form {
		kind: ReleaseKind;
		regions: string[];
		languages: string;
		revision: string;
		disc: number | null;
		hack: boolean;
		translated: boolean;
		version: string;
	}

	const fields = [
		'kind',
		'regions',
		'languages',
		'revision',
		'disc',
		'hack',
		'translated',
		'version'
	];
	const id = `edit-release-${Math.random().toString(36).slice(2)}`;
	const has = (flags: string[], flag: string) =>
		flags.some((f) => f.toLowerCase() === flag.toLowerCase());

	let regions = $state<string[]>([]);
	let loading = $state(false);
	let loadError = $state<unknown>(null);
	let form = $state<Form>(blank());
	let initial = $state<Form>(blank());
	let errors = $state<Record<string, string>>({});
	let error = $state<unknown>(null);
	let busy = $state(false);

	function blank(): Form {
		return {
			kind: 'release',
			regions: [],
			languages: '',
			revision: '',
			disc: null,
			hack: false,
			translated: false,
			version: ''
		};
	}

	function prefill(r: RomDetail): Form {
		const disc = r.flags.map((f) => /^Disc (\d+)$/i.exec(f)?.[1]).find((d) => d);
		return {
			kind: has(r.flags, 'Update') ? 'update' : has(r.flags, 'DLC') ? 'dlc' : 'release',
			regions: [...r.regions],
			languages: r.languages.join(','),
			revision: r.revision ?? '',
			disc: disc ? Number(disc) : null,
			hack: has(r.flags, 'Hack'),
			translated: has(r.flags, 'Translated'),
			version: r.version ?? ''
		};
	}

	async function loadRegions(): Promise<void> {
		loading = true;
		loadError = null;
		try {
			regionList ??= api.get<string[]>('/api/systems/regions');
			regions = await regionList;
		} catch (e) {
			regionList = null;
			loadError = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		if (!open || !rom) return;
		const target = rom;
		untrack(() => {
			initial = prefill(target);
			form = prefill(target);
			errors = {};
			error = null;
			busy = false;
			void loadRegions();
		});
	});

	/** B3: the list, then (checked) any stored region that isn't in it. */
	const shownRegions = $derived([
		...regions,
		...initial.regions.filter((r) => !regions.includes(r))
	]);

	function toggleRegion(region: string, checked: boolean): void {
		const picked = (r: string) => (r === region ? checked : form.regions.includes(r));
		// Stored ones still checked keep their order; new ones follow in the list order.
		form.regions = [
			...initial.regions.filter(picked),
			...shownRegions.filter((r) => picked(r) && !initial.regions.includes(r))
		];
	}

	const parseLanguages = (text: string) => [
		...new Set(
			text
				.split(',')
				.map((l) => l.trim())
				.filter((l) => l)
		)
	];
	const same = (a: string[], b: string[]) => a.length === b.length && a.every((v, i) => v === b[i]);

	/** B3 Submit: only the fields whose value differs from the prefill. */
	const changes = $derived.by(() => {
		const body: Record<string, unknown> = {};
		if (form.kind !== initial.kind) body.kind = form.kind;
		if (!same(form.regions, initial.regions)) body.regions = form.regions;
		const languages = parseLanguages(form.languages);
		if (!same(languages, parseLanguages(initial.languages))) body.languages = languages;
		if (form.revision.trim() !== initial.revision) body.revision = form.revision.trim() || null;
		// An emptied number input binds undefined.
		const disc = form.disc ?? null;
		if (fileSets && disc !== initial.disc) body.disc = disc;
		if (form.hack !== initial.hack) body.hack = form.hack;
		if (form.translated !== initial.translated) body.translated = form.translated;
		if (form.kind === 'update' && form.version.trim() !== initial.version)
			body.version = form.version.trim() || null;
		return body;
	});

	const tags = (f: Form, version: string): ReleaseTags => ({
		regions: f.regions,
		languages: parseLanguages(f.languages),
		revision: f.revision.trim() || null,
		hack: f.hack,
		translated: f.translated,
		version: f.kind === 'update' ? version.trim() || null : null
	});

	const preview = $derived(
		rom
			? editReleaseFileName(
					rom.fileName,
					rom.baseTitle,
					tags(initial, initial.version),
					tags(form, form.version)
				)
			: ''
	);

	async function save(): Promise<void> {
		if (!rom || busy) return;
		if (Object.keys(changes).length === 0) {
			open = false;
			return;
		}
		busy = true;
		error = null;
		errors = {};
		try {
			const done = await api.patch<{ fileName: string }>(
				`/api/games/${encodeURIComponent(gameId)}/roms/${rom.id}`,
				changes
			);
			toasts.show(`Release updated: ${done.fileName}`, 'success');
			open = false;
			onsaved(done.fileName, form.kind);
		} catch (e) {
			if (toasts.jobRunning(e)) return;
			if (isApiError(e) && e.status === 400) {
				errors = Object.fromEntries(
					fields.filter((f) => e.errors[f]?.[0]).map((f) => [f, e.errors[f][0]])
				);
				if (Object.keys(errors).length > 0) return;
			}
			error = e;
		} finally {
			busy = false;
		}
	}

	const kinds: [ReleaseKind, string][] = [
		['release', 'Release'],
		['update', 'Update'],
		['dlc', 'DLC']
	];
</script>

<BaseModal bind:open title="Edit release" confirmLabel="Save" {busy} onconfirm={save}>
	{#if rom}<p class="font-monospace small text-break mb-3">{rom.fileName}</p>{/if}
	{#if loadError}
		<ErrorAlert error={loadError} onretry={loadRegions} />
	{:else if loading}
		<Loading label="Loading regions…" />
	{:else}
		{#if error}<ErrorAlert {error} ondismiss={() => (error = null)} />{/if}

		<fieldset class="mb-3">
			<legend class="form-label fs-6">Kind</legend>
			{#each kinds as [value, label] (value)}
				<Checkbox
					type="radio"
					class={errors.kind ? 'is-invalid' : undefined}
					name="{id}-kind"
					id="{id}-kind-{value}"
					{label}
					inline
					{value}
					bind:group={form.kind}
				/>
			{/each}
			{#if errors.kind}<div class="invalid-feedback d-block">{errors.kind}</div>{/if}
		</fieldset>

		<fieldset class="mb-3">
			<legend class="form-label fs-6">Regions</legend>
			<div class="border rounded p-2 overflow-auto regions" class:border-danger={errors.regions}>
				{#each shownRegions as region (region)}
					<div class="form-check">
						<Checkbox
							id="{id}-region-{region}"
							checked={form.regions.includes(region)}
							onchange={(e) => toggleRegion(region, e.currentTarget.checked)}
						/>
						<label class="form-check-label" for="{id}-region-{region}">{region}</label>
					</div>
				{/each}
			</div>
			{#if errors.regions}<div class="invalid-feedback d-block">{errors.regions}</div>{/if}
		</fieldset>

		<div class="mb-3">
			<label class="form-label" for="{id}-languages">Languages</label>
			<TextInput
				class={errors.languages ? 'is-invalid' : undefined}
				id="{id}-languages"
				placeholder="En,Fr,De"
				bind:value={form.languages}
			/>
			{#if errors.languages}<div class="invalid-feedback">{errors.languages}</div>{/if}
		</div>

		<div class="mb-3">
			<label class="form-label" for="{id}-revision">Revision</label>
			<TextInput
				class={errors.revision ? 'is-invalid' : undefined}
				id="{id}-revision"
				maxlength={32}
				placeholder="Rev 1"
				bind:value={form.revision}
			/>
			{#if errors.revision}<div class="invalid-feedback">{errors.revision}</div>{/if}
		</div>

		{#if fileSets}
			<div class="mb-3">
				<label class="form-label" for="{id}-disc">Disc</label>
				<NumberInput
					class={errors.disc ? 'is-invalid' : undefined}
					id="{id}-disc"
					min="1"
					max="99"
					bind:value={form.disc}
				/>
				{#if errors.disc}<div class="invalid-feedback">{errors.disc}</div>{/if}
			</div>
		{/if}

		<div class="form-check form-switch">
			<Checkbox
				class={errors.hack ? 'is-invalid' : undefined}
				role="switch"
				id="{id}-hack"
				bind:checked={form.hack}
			/>
			<label class="form-check-label" for="{id}-hack">Hack</label>
			{#if errors.hack}<div class="invalid-feedback">{errors.hack}</div>{/if}
		</div>
		<div class="form-check form-switch mb-3">
			<Checkbox
				class={errors.translated ? 'is-invalid' : undefined}
				role="switch"
				id="{id}-translated"
				bind:checked={form.translated}
			/>
			<label class="form-check-label" for="{id}-translated">Translated</label>
			{#if errors.translated}<div class="invalid-feedback">{errors.translated}</div>{/if}
		</div>

		{#if form.kind === 'update'}
			<div class="mb-3">
				<label class="form-label" for="{id}-version">Version</label>
				<TextInput
					class={errors.version ? 'is-invalid' : undefined}
					id="{id}-version"
					placeholder="1.0.1"
					bind:value={form.version}
				/>
				{#if errors.version}<div class="invalid-feedback">{errors.version}</div>{/if}
			</div>
		{/if}

		{#if fileSets}
			<p class="small text-body-secondary mb-0">
				Disc releases keep their file name; only the tags change.
			</p>
		{:else if rom && preview === rom.fileName}
			<p class="small text-body-secondary mb-0">File name unchanged</p>
		{:else}
			<p class="small mb-0">New file name: <code class="text-break">{preview}</code></p>
		{/if}
	{/if}
</BaseModal>

<style>
	.regions {
		max-height: 12rem;
	}
</style>
