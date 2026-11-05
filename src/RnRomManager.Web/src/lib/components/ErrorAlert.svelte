<script lang="ts">
	import { ApiError } from '../api/errors';
	import Icon from './Icon.svelte';

	interface Props {
		/** An {@link ApiError} shows its ProblemDetails `title`; anything else a generic message. */
		error: unknown;
		onretry?: () => void;
		ondismiss?: () => void;
	}

	let { error, onretry, ondismiss }: Props = $props();

	const title = $derived(error instanceof ApiError ? error.title : 'Something went wrong.');
</script>

<div
	class="alert alert-danger d-flex align-items-center gap-2"
	class:alert-dismissible={ondismiss}
	role="alert"
>
	<Icon name="error" />
	<span class="me-auto">{title}</span>
	{#if onretry}
		<button type="button" class="btn btn-sm btn-outline-danger" onclick={onretry}>
			<Icon name="retry" /> Retry
		</button>
	{/if}
	{#if ondismiss}
		<button type="button" class="btn-close" aria-label="Dismiss" onclick={ondismiss}></button>
	{/if}
</div>
