<script lang="ts">
	import { api } from '../api/client';
	import ConfirmModal from './ConfirmModal.svelte';
	import CopyButton from './CopyButton.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import Icon from './Icon.svelte';
	import JsonTree from './JsonTree.svelte';
	import Loading from './Loading.svelte';

	/** GP4: the game ZIP's info.json, read-only, fetched each time the modal opens. */
	interface Props {
		open: boolean;
		gameId: string;
	}

	let { open = $bindable(), gameId }: Props = $props();

	let text = $state<string | null>(null);
	let error = $state<unknown>(null);

	async function load(): Promise<void> {
		text = null;
		error = null;
		try {
			text = await api.getText(`/api/games/${encodeURIComponent(gameId)}/info-json`);
		} catch (e) {
			error = e;
		}
	}

	$effect(() => {
		if (open) void load();
	});

	const parsed = $derived.by((): { ok: true; value: unknown } | { ok: false } => {
		if (text === null) return { ok: false };
		try {
			return { ok: true, value: JSON.parse(text) };
		} catch {
			return { ok: false };
		}
	});
</script>

<ConfirmModal bind:open title="info.json" size="lg">
	{#if error}
		<ErrorAlert {error} onretry={load} />
	{:else if text === null}
		<Loading />
	{:else}
		<div class="d-flex justify-content-end mb-2">
			<CopyButton value={text} label="info.json" />
		</div>
		{#if parsed.ok}
			<JsonTree value={parsed.value} />
		{:else}
			<div class="alert alert-warning d-flex gap-2" role="alert">
				<Icon name="warning" />
				<span>This info.json isn't valid JSON, so it's shown as stored.</span>
			</div>
			<pre class="small mb-0">{text}</pre>
		{/if}
	{/if}
</ConfirmModal>
