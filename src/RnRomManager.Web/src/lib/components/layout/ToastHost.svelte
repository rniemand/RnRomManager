<script lang="ts">
	import { toasts } from '#lib/stores/toasts.svelte.js';

	const background = {
		info: 'text-bg-primary',
		warning: 'text-bg-warning',
		danger: 'text-bg-danger',
		success: 'text-bg-success'
	};

	// B2: hover and focus hold a toast separately; it resumes only once neither does.
	const held = new Map<number, { hover: boolean; focus: boolean }>();

	function hold(id: number, key: 'hover' | 'focus', on: boolean) {
		const state = held.get(id) ?? { hover: false, focus: false };
		state[key] = on;
		if (state.hover || state.focus) {
			held.set(id, state);
			toasts.pause(id);
		} else if (held.delete(id)) {
			toasts.resume(id);
		}
	}

	function focusOut(id: number, event: FocusEvent) {
		const toast = event.currentTarget as HTMLElement;
		// Moving from the link to the close button stays inside the toast.
		if (!toast.contains(event.relatedTarget as Node | null)) hold(id, 'focus', false);
	}
</script>

<div
	class="toast-container position-fixed bottom-0 end-0 p-3"
	aria-live="polite"
	aria-atomic="false"
>
	{#each toasts.items as toast (toast.id)}
		<div
			class="toast show align-items-center border-0 {background[toast.kind]}"
			role="status"
			onmouseenter={() => hold(toast.id, 'hover', true)}
			onmouseleave={() => hold(toast.id, 'hover', false)}
			onfocusin={() => hold(toast.id, 'focus', true)}
			onfocusout={(event) => focusOut(toast.id, event)}
		>
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
