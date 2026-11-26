<script lang="ts">
	import type { HTMLInputAttributes } from 'svelte/elements';

	interface Props extends Omit<HTMLInputAttributes, 'type' | 'size' | 'value' | 'class'> {
		/** `search` is `SearchInput`'s. */
		type?: 'text' | 'password' | 'url' | 'email' | 'date' | 'time' | 'search';
		value?: string | null;
		size?: 'sm' | 'lg';
		class?: string;
		ref?: HTMLInputElement;
	}

	let {
		type = 'text',
		value = $bindable(),
		size,
		class: className,
		ref = $bindable(),
		...rest
	}: Props = $props();
</script>

<input
	bind:this={ref}
	{type}
	bind:value
	class={['form-control', size && `form-control-${size}`, className]}
	{...rest}
/>
