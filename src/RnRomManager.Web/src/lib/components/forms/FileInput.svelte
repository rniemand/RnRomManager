<script lang="ts">
	import type { HTMLInputAttributes } from 'svelte/elements';

	/** `accept`, `multiple` and `onchange` go through `...rest`; read the files from `ref` or the event. */
	interface Props extends Omit<HTMLInputAttributes, 'type' | 'size' | 'value' | 'class'> {
		size?: 'sm' | 'lg';
		class?: string;
		ref?: HTMLInputElement;
	}

	let { size, class: className, ref = $bindable(), ...rest }: Props = $props();
</script>

<input
	bind:this={ref}
	type="file"
	class={['form-control', size && `form-control-${size}`, className]}
	{...rest}
/>
