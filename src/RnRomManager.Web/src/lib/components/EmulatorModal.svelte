<script lang="ts">
	import { untrack } from 'svelte';
	import { api } from '../api/client';
	import { isApiError } from '../api/errors';
	import type { RomDetail, SaveItem } from '#lib/types/index.js';
	import type { SaveKind } from '#lib/enums/index.js';
	import { playUrl, recordPlay, trimScreenshot, type PlaySave } from '../emulator';
	import { kindLabels, maxSaveBytes, savesApi } from '../saves';
	import { auth } from '../stores/auth.svelte';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import Icon from './Icon.svelte';
	import Loading from './Loading.svelte';

	/**
	 * Browser emulators B4: the play page in a same-origin iframe. The iframe exists only while open, so closing
	 * the modal stops the emulator and its audio.
	 */
	interface Props {
		open: boolean;
		gameId: string;
		core: string | null;
		/** All it reads (through `playUrl`), so a Library card can pass its list item's primary release (C2). */
		rom: Pick<RomDetail, 'id' | 'fileName'> | null;
		/** Sega CD B6: the release is external (a disc), so the play URL carries `disc=1`. */
		disc: boolean;
		/** Saves B17: a library save the play page loads when the game starts. */
		save?: PlaySave | null;
		/**
		 * Multi-disc M8: a PlayStation disc set's playlist name; `rom` is then the set's Disc 1, whose release the battery
		 * saves use. The page never passes `save` with it, and state saves aren't offered.
		 */
		discSet?: string | null;
		/** Saves B19: called after each successful "save to library". */
		onsaved?: () => void;
		/** E2: called after a screenshot was saved as a Snap. */
		onimagesaved?: () => void;
	}

	let {
		open = $bindable(),
		gameId,
		core,
		rom,
		disc,
		save = null,
		discSet = null,
		onsaved,
		onimagesaved
	}: Props = $props();

	/**
	 * B22: one play per opening (false → true, a mount already open included) while the player shows; a prop changing
	 * while open (a fresh `rom` literal, a refetch) records nothing more.
	 */
	let wasOpen = false;
	$effect(() => {
		const isOpen = open;
		untrack(() => {
			if (isOpen && !wasOpen && rom && core && gameId) recordPlay(gameId, rom.id);
			wasOpen = isOpen;
		});
	});

	/** Saves B18: neither DeSmuME's nor Opera's battery save goes through EmulatorJS's save file. */
	const batteryExport = $derived(core !== 'desmume' && core !== 'opera');

	type ExportResult =
		| { ok: true; bytes: ArrayBuffer; extension: string }
		| { ok: false; reason: 'not-started' | 'busy' | 'unsupported' | 'empty' | 'error' | 'timeout' };

	const reasons: Record<string, string> = {
		'not-started': 'Start the game first.',
		busy: 'The emulator is saving. Try again in a moment.',
		unsupported: "This emulator can't save states.",
		empty: 'There is no save to copy yet.',
		error: "Couldn't read the save from the emulator.",
		// Release editing B23: the player never answered.
		timeout: "Couldn't read the save from the emulator."
	};

	/** How long an export may take before it is given up (a 256 MiB state is copied once). */
	const exportTimeoutMs = 30000;

	let exporting = $state(false);

	/** Saves B18: asks the play page for the running game's state or battery save; E2: or a screenshot. */
	function requestExport(
		target: Window,
		kind: SaveKind | 'screenshot',
		timeoutMs = exportTimeoutMs
	): Promise<ExportResult> {
		return new Promise((resolve) => {
			const onMessage = (event: MessageEvent) => {
				if (event.source !== target || event.origin !== location.origin) return;
				const data = event.data;
				if (data?.type !== 'rnrm-emulator-export-result' || data.kind !== kind) return;
				done(
					data.ok === true && data.bytes instanceof ArrayBuffer
						? { ok: true, bytes: data.bytes, extension: String(data.extension ?? '') }
						: { ok: false, reason: data.reason in reasons ? data.reason : 'error' }
				);
			};
			const done = (result: ExportResult) => {
				window.removeEventListener('message', onMessage);
				clearTimeout(timer);
				resolve(result);
			};
			window.addEventListener('message', onMessage);
			const timer = setTimeout(() => done({ ok: false, reason: 'timeout' }), timeoutMs);
			target.postMessage({ type: 'rnrm-emulator-export', kind }, location.origin);
		});
	}

	const pad = (n: number) => String(n).padStart(2, '0');
	/** Saves B19: `<State|Battery> <local yyyy-MM-dd HH:mm>`. */
	function saveName(kind: SaveKind, now = new Date()): string {
		const date = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
		return `${kindLabels[kind]} ${date} ${pad(now.getHours())}:${pad(now.getMinutes())}`;
	}

	const isMine = (s: SaveItem) => {
		const me = auth.me?.username.toLowerCase();
		return me !== undefined && s.uploadedBy?.toLowerCase() === me;
	};

	/**
	 * Saves B19: stores exported bytes. Replaces `replaceId` when given, else overwrites the signed-in user's newest save of
	 * this kind for this release (the list is newest first; S5: an admin lists everyone's saves, so ownership is checked
	 * too); adds one when there is none.
	 */
	async function store(
		kind: SaveKind,
		bytes: ArrayBuffer,
		extension: string,
		list: SaveItem[],
		replaceId?: string
	): Promise<void> {
		if (!rom) return;
		const file = new File([bytes], `save${extension}`);
		const existing =
			replaceId ??
			list.find((s) => s.kind === kind && s.releaseFileName === rom.fileName && isMine(s))?.id;
		if (existing) await savesApi.replace(gameId, existing, file);
		else
			await savesApi.add(gameId, {
				romId: rom.id,
				kind,
				name: saveName(kind),
				description: '',
				file,
				fileName: file.name
			});
	}

	/** Release editing B23: the running "save to library", which a close waits for. */
	let saving: Promise<void> | null = null;

	function saveToLibrary(kind: SaveKind): Promise<void> {
		if (exporting) return saving ?? Promise.resolve();
		saving = copyToLibrary(kind).finally(() => (saving = null));
		return saving;
	}

	/** Saves B19: copies the running game's save into the library through the upload route. */
	async function copyToLibrary(kind: SaveKind): Promise<void> {
		const target = frame?.contentWindow;
		if (!target || !rom) return;
		exporting = true;
		try {
			const result = await requestExport(target, kind);
			if (!result.ok) {
				toasts.show(reasons[result.reason], 'warning');
				return;
			}
			if (result.bytes.byteLength > maxSaveBytes) {
				toasts.show('This save is larger than 256 MiB', 'danger');
				return;
			}
			await store(kind, result.bytes, result.extension, await savesApi.list(gameId));
			toasts.show('Saved to library', 'success');
			onsaved?.();
		} catch (e) {
			if (!toasts.jobRunning(e))
				toasts.show(isApiError(e) ? e.title : "Couldn't save to the library.", 'danger');
		} finally {
			exporting = false;
		}
	}

	/** Release editing B22: the battery save the player was opened with when there's no explicit `save`. */
	let autoSave = $state<PlaySave | null>(null);
	/** B22: the iframe waits for the auto-load lookup. */
	let lookingUp = $state(false);
	const playSave = $derived(save ?? autoSave);

	$effect(() => {
		// Once per opening: a fresh `rom` literal while open must not reload the player.
		if (!open) return;
		untrack(() => {
			const target = rom;
			autoSave = null;
			if (!target || !core) return;
			if (save || !batteryExport) {
				lookingUp = false;
				return;
			}
			const current = session;
			lookingUp = true;
			savesApi
				.list(gameId)
				.then(
					(list) => {
						if (current !== session) return;
						// B22: the user's newest battery save of this release (the list is newest first).
						const found = list.find(
							(s) =>
								s.kind === 'battery' &&
								s.releaseFileName.toLowerCase() === target.fileName.toLowerCase() &&
								isMine(s)
						);
						if (found) autoSave = { saveId: found.id, saveKind: 'battery' };
					},
					() => {} // B22: plays without a save
				)
				.finally(() => {
					if (current === session) lookingUp = false;
				});
		});
	});

	/** Release editing B23: copies the battery save into the library before the player goes. */
	async function storeBeforeClose(): Promise<void> {
		if (saving) await saving;
		const target = frame?.contentWindow;
		if (!target || !rom) return;
		const result = await requestExport(target, 'battery', closeExportTimeoutMs);
		if (!result.ok) {
			// Not started, no battery RAM, no support or no answer: nothing to keep.
			if (result.reason === 'busy' || result.reason === 'error')
				toasts.show("Couldn't store the battery save", 'warning');
			return;
		}
		if (result.bytes.byteLength === 0) return;
		try {
			if (result.bytes.byteLength > maxSaveBytes) throw new Error('too large');
			const list = await savesApi.list(gameId);
			// The played save is replaced only when it's the user's own battery save (an Admin may play another user's).
			const played =
				playSave?.saveKind === 'battery'
					? list.find((s) => s.id === playSave.saveId && isMine(s))?.id
					: undefined;
			await store('battery', result.bytes, result.extension, list, played);
			toasts.show('Battery save stored in the library', 'success');
			onsaved?.();
		} catch {
			toasts.show("Couldn't store the battery save", 'warning');
		}
	}

	/** Release editing B23: how long a close waits for the battery save. */
	const closeExportTimeoutMs = 5000;

	let frame = $state<HTMLIFrameElement | null>(null);

	/** E2: the screenshot waiting for Save as Snap or Discard; dropped when the modal closes. */
	type Shot = { url: string; bytes: ArrayBuffer };
	/** Release editing B25, B26: the screenshot as taken and without its border (null when nothing was trimmed). */
	let preview = $state<{ original: Shot; trimmed: Shot | null } | null>(null);
	let keepBorder = $state(false);
	/** B26: what the preview shows and Save as Snap uploads. */
	const shown = $derived(
		preview && (keepBorder || !preview.trimmed) ? preview.original : (preview?.trimmed ?? null)
	);
	let uploading = $state(false);
	/** Bumped on every close, so a screenshot answered after it is ignored. */
	let session = 0;

	function discard(): void {
		if (preview) {
			URL.revokeObjectURL(preview.original.url);
			if (preview.trimmed) URL.revokeObjectURL(preview.trimmed.url);
		}
		preview = null;
		keepBorder = false;
	}

	const shot = (bytes: ArrayBuffer): Shot => ({
		url: URL.createObjectURL(new Blob([bytes], { type: 'image/png' })),
		bytes
	});

	$effect(() => {
		if (open) return;
		untrack(() => {
			session++;
			discard();
		});
	});

	/** E2: asks the play page for a PNG and shows it over the game; a new one replaces the last. */
	async function screenshot(): Promise<void> {
		const target = frame?.contentWindow;
		if (!target || exporting) return;
		const current = session;
		exporting = true;
		try {
			const result = await requestExport(target, 'screenshot');
			if (current !== session) return;
			if (!result.ok) {
				toasts.show(
					result.reason === 'error' || result.reason === 'timeout'
						? "Couldn't take a screenshot."
						: reasons[result.reason],
					'warning'
				);
				return;
			}
			const trimmed = await trimScreenshot(result.bytes);
			if (current !== session) return;
			discard();
			preview = { original: shot(result.bytes), trimmed: trimmed ? shot(trimmed) : null };
		} finally {
			exporting = false;
		}
	}

	/** E2: uploads the preview as a Snap, as GameImages' upload builds its form. */
	async function saveSnap(): Promise<void> {
		if (!shown || uploading) return;
		const form = new FormData();
		form.append('file', new File([shown.bytes], 'screenshot.png', { type: 'image/png' }));
		form.append('type', 'Snap');
		uploading = true;
		try {
			await api.post(`/api/games/${encodeURIComponent(gameId)}/images`, form);
			toasts.show("Screenshot saved to the game's images", 'success');
			discard();
			onimagesaved?.();
		} catch (e) {
			if (!toasts.jobRunning(e))
				toasts.show(isApiError(e) ? e.title : "Couldn't save the screenshot.", 'danger');
		} finally {
			uploading = false;
		}
	}

	/** 3DO B11: how long the modal waits for the player's reply before closing anyway. */
	const saveTimeoutMs = 3000;

	/**
	 * 3DO B11: Opera writes its NVRAM only on reset, so the player resets and syncs /data/saves before the iframe
	 * goes. Resolves on its reply or after the timeout, whichever is first.
	 */
	function saveBeforeClose(): Promise<void> {
		const target = frame?.contentWindow;
		if (!target) return Promise.resolve();
		return new Promise((resolve) => {
			const onMessage = (event: MessageEvent) => {
				if (event.source !== target || event.origin !== location.origin) return;
				if (event.data?.type === 'rnrm-emulator-closed') done();
			};
			const done = () => {
				window.removeEventListener('message', onMessage);
				clearTimeout(timer);
				resolve();
			};
			window.addEventListener('message', onMessage);
			const timer = setTimeout(done, saveTimeoutMs);
			target.postMessage({ type: 'rnrm-emulator-close' }, location.origin);
		});
	}
</script>

<ConfirmModal
	bind:open
	title="Play {discSet ?? rom?.fileName ?? ''}"
	size="xl"
	beforeclose={core === 'opera' ? saveBeforeClose : batteryExport ? storeBeforeClose : undefined}
>
	<!-- U3: the save and screenshot buttons sit in the dialog's footer, left of Close. -->
	{#snippet footer()}
		{#if open && rom && core}
			{#if !discSet}
				<button
					type="button"
					class="btn btn-sm btn-outline-primary"
					disabled={exporting}
					onclick={() => saveToLibrary('state')}
				>
					<Icon name="download" /> Save state to library
				</button>
			{/if}
			{#if batteryExport}
				<button
					type="button"
					class="btn btn-sm btn-outline-primary"
					disabled={exporting}
					onclick={() => saveToLibrary('battery')}
				>
					<Icon name="download" /> Save battery to library
				</button>
			{/if}
			{#if auth.isAdmin}
				<button
					type="button"
					class="btn btn-sm btn-outline-primary"
					disabled={exporting}
					onclick={screenshot}
				>
					<Icon name="screenshot" /> Screenshot
				</button>
			{/if}
		{/if}
	{/snippet}
	<!-- Cap the width so the 4:3 frame fits the viewport height (dialog chrome ≈ 14rem): Start Game needs no scrolling. -->
	<div class="ratio ratio-4x3 mx-auto" style="max-width: calc((100vh - 14rem) * 4 / 3)">
		{#if open && rom && core && lookingUp}
			<div class="d-flex align-items-center justify-content-center"><Loading /></div>
		{:else if open && rom && core}
			<iframe
				bind:this={frame}
				title="Emulator: {discSet ?? rom.fileName}"
				allow="fullscreen; gamepad; autoplay"
				src={playUrl(gameId, rom, core, disc || discSet !== null, playSave, discSet)}
			></iframe>
			{#if preview && shown}
				<!-- E2: over the game, inside the emulator modal (not a second Bootstrap modal). -->
				<div class="bg-body d-flex flex-column align-items-center justify-content-center gap-2 p-2">
					<img src={shown.url} alt="Screenshot preview" class="img-fluid" style="max-height: 85%" />
					{#if preview.trimmed}
						<!-- Release editing B26: only when the border was trimmed. -->
						<div class="form-check">
							<input
								class="form-check-input"
								type="checkbox"
								id="screenshot-keep-border"
								bind:checked={keepBorder}
							/>
							<label class="form-check-label" for="screenshot-keep-border">Keep border</label>
						</div>
					{/if}
					<div class="d-flex gap-2">
						<button
							type="button"
							class="btn btn-sm btn-primary"
							disabled={uploading}
							onclick={saveSnap}>Save as Snap</button
						>
						<button type="button" class="btn btn-sm btn-outline-secondary" onclick={discard}
							>Discard</button
						>
					</div>
				</div>
			{/if}
		{/if}
	</div>
</ConfirmModal>
