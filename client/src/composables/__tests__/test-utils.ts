import { flushPromises, mount } from "@vue/test-utils";
import { getActivePinia } from "pinia";
import { defineComponent, nextTick } from "vue";

export async function flushAll(): Promise<void> {
  await nextTick();
  await flushPromises();
}

export async function mountComposable<T>(useComposable: () => T): Promise<{
  result: T;
  wrapper: ReturnType<typeof mount>;
}> {
  let result!: T;

  const wrapper = mount(
    defineComponent({
      name: "ComposableHarness",
      setup() {
        result = useComposable();
        return () => null;
      },
    }),
    {
      global: {
        plugins: getActivePinia() ? [getActivePinia()!] : [],
      },
    },
  );

  await flushAll();

  return {
    result,
    wrapper,
  };
}
