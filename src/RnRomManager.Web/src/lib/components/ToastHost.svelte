<script lang="ts">
	import { toasts } from '../stores/toasts.svelte';

	const background = {
		info: 'text-bg-primary',
		warning: 'text-bg-warning',
		danger: 'text-bg-danger',
		success: 'text-bg-success'
	};
</script>

<div
	class="toast-container position-fixed bottom-0 end-0 p-3"
	aria-live="polite"
	aria-atomic="false"
>
	{#each toasts.items as toast (toast.id)}
		<div class="toast show align-items-center border-0 {background[toast.kind]}" role="status">
			<div class="d-flex">
				<div class="toast-body">
					{toast.text}
					{#if toast.link}
						<!-- The href is an app path the caller already resolve()d. -->
						<!-- eslint-disable svelte/no-navigation-without-resolve -->
						<a
							class="ms-1 fw-semibold"
							class:link-light={toast.kind !== 'warning'}
							href={toast.link.href}
							onclick={() => toasts.dismiss(toast.id)}>{toast.link.text}</a
						>
						<!-- eslint-enable svelte/no-navigation-without-resolve -->
					{/if}
				</div>
				<button
					type="button"
					class="btn-close me-2 m-auto"
					class:btn-close-white={toast.kind !== 'warning'}
					aria-label="Close"
					onclick={() => toasts.dismiss(toast.id)}
				></button>
			</div>
		</div>
	{/each}
</div>
