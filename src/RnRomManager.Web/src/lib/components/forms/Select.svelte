<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { HTMLSelectAttributes } from 'svelte/elements';

	interface Props extends Omit<HTMLSelectAttributes, 'size' | 'value' | 'class'> {
		value?: unknown;
		/** Rendered as `<option>`s; otherwise pass the `<option>`s as children. */
		options?: { value: string | number; label: string; disabled?: boolean }[];
		size?: 'sm';
		class?: string;
		ref?: HTMLSelectElement;
		children?: Snippet;
	}

	let {
		value = $bindable(),
		options,
		size,
		class: className,
		ref = $bindable(),
		children,
		...rest
	}: Props = $props();
</script>

<select
	bind:this={ref}
	bind:value
	class={['form-select', size && `form-select-${size}`, className]}
	{...rest}
>
	{#if options}
		{#each options as option (option.value)}
			<option value={option.value} disabled={option.disabled}>{option.label}</option>
		{/each}
	{:else}
		{@render children?.()}
	{/if}
</select>
