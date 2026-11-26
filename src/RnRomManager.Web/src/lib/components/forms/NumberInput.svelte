<script lang="ts">
	import type { HTMLInputAttributes } from 'svelte/elements';

	interface Props extends Omit<HTMLInputAttributes, 'type' | 'size' | 'value' | 'class'> {
		value?: number | null;
		size?: 'sm' | 'lg';
		class?: string;
		ref?: HTMLInputElement;
	}

	let { value = $bindable(), size, class: className, ref = $bindable(), ...rest }: Props = $props();
</script>

<input
	bind:this={ref}
	type="number"
	bind:value
	class={['form-control', size && `form-control-${size}`, className]}
	{...rest}
/>
