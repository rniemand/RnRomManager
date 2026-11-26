<script lang="ts">
	import { fromAction } from 'svelte/attachments';
	import Button from '#lib/components/forms/Button.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import { tick } from 'svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { GameDetail } from '#lib/types/index.js';
	import { titleError } from '#lib/gameEdit.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { tooltip } from '#lib/tooltip.js';
	import Icon from '#lib/icons/Icon.svelte';

	interface Props {
		game: GameDetail;
		onsaved: (detail: GameDetail) => void;
		/** The game is gone (404): the page reloads and asks for a retry. */
		onstale: () => void;
	}

	let { game, onsaved, onstale }: Props = $props();

	let editing = $state(false);
	let value = $state('');
	let error = $state<string | undefined>();
	let busy = $state(false);
	let input = $state<HTMLInputElement>();
	/** T4: Enter, Escape or the first blur handles the edit; any later blur is a no-op. */
	let handled = false;

	async function start(): Promise<void> {
		value = game.title;
		error = undefined;
		handled = false;
		editing = true;
		await tick();
		input?.focus();
		input?.select();
	}

	async function save(): Promise<void> {
		if (handled) return;
		handled = true;
		const title = value.trim();
		if (title === game.title) {
			editing = false;
			return;
		}
		error = titleError(title);
		if (error) {
			handled = false;
			return;
		}
		busy = true;
		try {
			const detail = await api.patch<GameDetail>(`/api/games/${encodeURIComponent(game.id)}`, {
				title
			});
			toasts.show('Saved.', 'success');
			editing = false;
			onsaved(detail);
		} catch (e) {
			if (toasts.jobRunning(e)) {
				editing = false;
			} else if (isApiError(e) && e.status === 404) {
				editing = false;
				onstale();
			} else if (isApiError(e, 'ValidationFailed') && e.errors.title?.[0]) {
				error = e.errors.title[0];
				handled = false;
			} else {
				toasts.show(isApiError(e) ? e.title : 'Something went wrong.', 'danger');
				handled = false;
			}
		} finally {
			busy = false;
		}
		if (editing) {
			await tick();
			input?.focus();
		}
	}

	function cancel(): void {
		handled = true;
		editing = false;
	}
</script>

{#if editing}
	<form
		class="flex-grow-1"
		novalidate
		onsubmit={(e) => {
			e.preventDefault();
			void save();
		}}
	>
		<TextInput
			size="lg"
			class={error ? 'is-invalid' : undefined}
			bind:ref={input}
			aria-label="Title"
			maxlength={255}
			disabled={busy}
			bind:value
			onkeydown={(e) => {
				if (e.key === 'Escape') {
					e.preventDefault();
					e.stopPropagation();
					cancel();
				}
			}}
			onblur={() => void save()}
		/>
		{#if error}<div class="invalid-feedback">{error}</div>{/if}
	</form>
{:else}
	<h1 class="h3 mb-0">
		<Button
			variant="link"
			class="p-0 text-reset text-decoration-none title-edit"
			{@attach fromAction(tooltip, () => 'Click to edit the title')}
			onclick={start}
			>{game.displayTitle}<Icon name="edit" class="ms-2 text-body-secondary small" /></Button
		>
	</h1>
{/if}

<style>
	:global(.title-edit) {
		font: inherit;
	}
	:global(.title-edit i) {
		opacity: 0;
	}
	:global(.title-edit:hover i),
	:global(.title-edit:focus-visible i) {
		opacity: 1;
	}
	@media (hover: none) {
		:global(.title-edit i) {
			opacity: 1;
		}
	}
</style>
