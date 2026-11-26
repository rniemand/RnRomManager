<script lang="ts">
	import type { HTMLTextareaAttributes } from 'svelte/elements';

	interface Props extends Omit<HTMLTextareaAttributes, 'value' | 'class'> {
		value?: string | null;
		class?: string;
		ref?: HTMLTextAreaElement;
	}

	let { value = $bindable(), class: className, ref = $bindable(), ...rest }: Props = $props();
</script>

<textarea bind:this={ref} bind:value class={['form-control', className]} {...rest}></textarea>
