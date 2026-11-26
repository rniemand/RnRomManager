<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import { ApiError } from '#lib/api/errors.js';
	import Icon from '#lib/icons/Icon.svelte';

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
		<Button variant="danger" outline size="sm" icon="retry" onclick={onretry}>Retry</Button>
	{/if}
	{#if ondismiss}
		<Button class="btn-close" aria-label="Dismiss" onclick={ondismiss} />
	{/if}
</div>
