export default function activate(pi) {
  pi.registerTool({
    name: 'custom_ui_questionnaire_tool',
    label: 'Custom UI Questionnaire',
    description: 'Questionnaire-style custom UI fixture',
    parameters: { type: 'object', properties: {} },
    execute: async (_toolCallId, _params, _signal, _onUpdate, ctx) => {
      const result = await ctx.ui.custom((tui, _theme, keybindings, done) => {
        let selected = 'Alpha';

        const render = () => [
          '# Pick a choice',
          '> Preview: `markdown-looking` lines stay intact',
          selected === 'Alpha' ? '> Alpha' : '  Alpha',
          selected === 'Beta' ? '> Beta' : '  Beta',
        ];

        return {
          render,
          handleInput: (data) => {
            if (data === keybindings.down) {
              selected = 'Beta';
              tui.requestRender();
            }

            if (data === keybindings.enter) {
              done({ selected });
            }
          },
        };
      }, {
        overlay: true,
        keybindings: {
          down: '\u001b[B',
          enter: '\r',
        },
      });

      return { content: [{ type: 'text', text: result.selected }] };
    },
  });
}
