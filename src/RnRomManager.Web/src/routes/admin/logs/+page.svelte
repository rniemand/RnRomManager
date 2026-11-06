<script lang="ts">
	import { onDestroy, onMount, tick } from 'svelte';
	import { api } from '#lib/api/client.js';
	import type { LogEntry } from '#lib/api/types.js';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import { auth } from '#lib/stores/auth.svelte.js';

	type Level = LogEntry['level'];

	const levels: Level[] = ['Trace', 'Debug', 'Info', 'Warn', 'Error', 'Fatal'];
	const badge: Record<Level, string> = {
		Trace: 'text-bg-secondary',
		Debug: 'text-bg-secondary',
		Info: 'text-bg-info',
		Warn: 'text-bg-warning',
		Error: 'text-bg-danger',
		Fatal: 'text-bg-danger'
	};
	/** Lines kept in the browser (L3); older ones drop off the top. */
	const maxLines = 2000;
	// EventSource.CLOSED: the browser gave up (e.g. 401/403) and won't reconnect by itself.
	const closed = 2;

	let lines = $state<LogEntry[]>([]);
	let pending: LogEntry[] = [];
	let paused = $state(false);
	let minLevel = $state<Level>('Info');
	let fetchError = $state<unknown>(null);
	let status = $state<'connecting' | 'open' | 'reconnecting' | 'closed'>('connecting');
	let source: EventSource | null = null;
	let view = $state<HTMLDivElement>();
	let atBottom = true;

	const visible = $derived(
		lines.filter((l) => levels.indexOf(l.level) >= levels.indexOf(minLevel))
	);

	/** Adds entries by id (L5): duplicates are skipped and the list stays sorted, so fetch, backlog and retry overlap safely. */
	function merge(entries: LogEntry[]): void {
		const next = [...lines];
		for (const entry of entries) {
			const last = next.at(-1);
			if (!last || entry.id > last.id) {
				next.push(entry);
				continue;
			}
			const at = next.findIndex((l) => l.id >= entry.id);
			if (next[at].id !== entry.id) next.splice(at, 0, entry);
		}
		lines = next.slice(-maxLines);
	}

	async function loadRecent(): Promise<void> {
		fetchError = null;
		try {
			merge(await api.get<LogEntry[]>('/api/admin/logs/recent'));
		} catch (e) {
			fetchError = e;
		}
	}

	function connect(): void {
		source?.close();
		status = 'connecting';
		const es = new EventSource('/api/admin/logs/stream');
		es.onopen = () => (status = 'open');
		es.onmessage = (event: MessageEvent<string>) => {
			const entry = JSON.parse(event.data) as LogEntry;
			if (paused) pending = [...pending, entry].slice(-maxLines);
			else merge([entry]);
		};
		es.onerror = () => (status = es.readyState === closed ? 'closed' : 'reconnecting');
		source = es;
	}

	function togglePause(): void {
		paused = !paused;
		if (!paused) {
			merge(pending);
			pending = [];
		}
	}

	function onscroll(): void {
		if (view) atBottom = view.scrollHeight - view.scrollTop - view.clientHeight < 8;
	}

	$effect(() => {
		void visible.length;
		if (!atBottom || !view) return;
		void tick().then(() => {
			if (view) view.scrollTop = view.scrollHeight;
		});
	});

	onMount(() => {
		if (auth.isAdmin) void loadRecent().then(connect);
	});

	onDestroy(() => source?.close());
</script>

<svelte:head><title>Logs · Admin · RnRomManager</title></svelte:head>

<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
	<!-- The API sends the ZIP as an attachment, so a plain link downloads it. -->
	<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- API endpoint, not an app route -->
	<a class="btn btn-outline-primary" href="/api/admin/logs" download>
		<Icon name="download" /> Download logs
	</a>
	<button type="button" class="btn btn-outline-secondary" onclick={togglePause}>
		{paused ? 'Resume' : 'Pause'}
	</button>
	<div class="d-flex align-items-center gap-2">
		<label class="small text-body-secondary text-nowrap" for="log-level">Minimum level</label>
		<select id="log-level" class="form-select form-select-sm w-auto" bind:value={minLevel}>
			{#each levels as level (level)}
				<option value={level}>{level}</option>
			{/each}
		</select>
	</div>
	{#if status === 'reconnecting'}
		<span class="badge text-bg-warning" role="status">Reconnecting…</span>
	{/if}
</div>

{#if fetchError}
	<ErrorAlert error={fetchError} onretry={loadRecent} />
{/if}
{#if status === 'closed'}
	<ErrorAlert error={null} onretry={connect} />
{/if}

<!-- Focusable so keyboard users can scroll it (axe scrollable-region-focusable). -->
<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
<div
	bind:this={view}
	class="card font-monospace small overflow-auto p-2"
	style="height: 60vh"
	role="log"
	aria-label="Live log"
	tabindex="0"
	{onscroll}
>
	{#each visible as line (line.id)}
		<div class="text-break">
			<span class="text-body-secondary">{line.time.slice(11, 23)}</span>
			<span class="badge {badge[line.level]}">{line.level}</span>
			<span class="text-body-secondary">{line.logger}</span>
			{line.message}
			{#if line.exception}<pre class="mb-0 ms-4 small">{line.exception}</pre>{/if}
		</div>
	{:else}
		<p class="text-body-secondary mb-0">
			{lines.length ? `No log events at ${minLevel} or above.` : 'No log events yet.'}
		</p>
	{/each}
</div>
