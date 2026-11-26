<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { HTMLInputAttributes } from 'svelte/elements';

	interface Props extends Omit<HTMLInputAttributes, 'type' | 'checked' | 'class' | 'value'> {
		/** With a label: a `form-check` wrapper and a `form-check-label` (needs `id`). Without: the bare input. */
		label?: string;
		/** `radio` reproduces `bind:group` (which Svelte 5 can't forward) through `group` and `value`. */
		type?: 'checkbox' | 'radio';
		checked?: boolean;
		indeterminate?: boolean;
		group?: unknown;
		value?: unknown;
		/** Adds `form-check-inline` to the wrapper. */
		inline?: boolean;
		/** Appended to the input's `form-check-input`. */
		class?: string;
		/** Appended to the wrapper's classes (e.g. `mb-3`). */
		wrapperClass?: string;
		ref?: HTMLInputElement;
		/** Rendered after the label inside the wrapper (e.g. `invalid-feedback`). */
		children?: Snippet;
	}

	let {
		label,
		type = 'checkbox',
		checked = $bindable(false),
		indeterminate = $bindable(false),
		group = $bindable(),
		value,
		inline = false,
		class: className,
		wrapperClass,
		ref = $bindable(),
		children,
		onchange,
		...rest
	}: Props = $props();

	const inputClass = $derived(['form-check-input', className]);

	function pick(event: Event & { currentTarget: EventTarget & HTMLInputElement }): void {
		group = value;
		onchange?.(event);
	}
</script>

{#snippet input()}
	{#if type === 'radio'}
		<input
			bind:this={ref}
			type="radio"
			class={inputClass}
			value={String(value)}
			checked={group === value}
			onchange={pick}
			{...rest}
		/>
	{:else}
		<input
			bind:this={ref}
			type="checkbox"
			class={inputClass}
			bind:checked
			bind:indeterminate
			{onchange}
			{...rest}
		/>
	{/if}
{/snippet}

{#if label}
	<div class={['form-check', inline && 'form-check-inline', wrapperClass]}>
		{@render input()}
		<label class="form-check-label" for={rest.id}>{label}</label>
		{@render children?.()}
	</div>
{:else}
	{@render input()}
{/if}
